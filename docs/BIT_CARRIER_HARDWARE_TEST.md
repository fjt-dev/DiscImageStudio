# 絵柄領域のビット搬送: 実機検証の手順書

[BIT_CARRIER.md](BIT_CARRIER.md) の機能を、実際のディスクとドライブで確かめるための手順です。

**この機能は、実ディスクではまだ一度も試されていません。** これまでの検証は、生成したイメージファイルの上で、符号化・復号・密度の保存を確かめたものだけです。この手順は、その先にある「ドライブとメディアを通しても成り立つか」を確かめます。

## 何を確かめるか

| # | 仮説 | 確かめる段階 | 外れたとき |
|---|---|---|---|
| H1 | ドライブが返す 2048 バイトのセクタは、書き込んだバイト列と一致する | 段階 A | 設計の前提が崩れます。ここで止めて原因を調べます。（ドライブはスクランブルを自分で掛けて外すので、これは PSN の前提とは無関係に成り立つはずです） |
| H2 | 埋め込んだファイルを、ディスクから読み出して取り出せる | 段階 B | H1 が成り立つのに外れるなら、DVD は PSN の前提（`--psn-offset`）か、符号化の不具合です |
| H3 | 並べ替えても、ディスク上の絵は見た目が変わらない | 段階 C | 容量の一部を諦める設計変更が要ります |
| H4 | 複数のドライブ・メディアで再現する | 段階 D | 相性の記録を残します |

H1 → H2 → H3 の順に進めてください。H1 が崩れたら、後の段階は意味を持ちません。

## 準備

- **メディア**: 最初は安価な CD-R を勧めます（書き込みが速く、失敗しても痛手が小さい）。DVD は CD で H1・H2 が通ってからにします。メディアは一度書くと使えません。
- **ドライブ**: 書き込み用 1 台。読み出し用に、できれば別の 1 台（同じドライブで読むと、そのドライブ固有の癖が見えません）。
- **ツール**: `disc-image-studio`（CLI）。本リポジトリをビルドするか、Releases の CLI を使います。
  ```bash
  dotnet publish src/DiscImageStudio.Cli -c Release -r linux-x64 --self-contained -o out   # osx-arm64 / win-x64 も同様
  ```
- **検証用ファイル**: 乱数で作り、SHA-256 を控えます。

  ```bash
  head -c 262144 /dev/urandom > payload.bin && sha256sum payload.bin        # Linux / macOS (shasum -a 256)
  ```
  ```powershell
  $b = New-Object byte[] 262144; (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes('payload.bin', $b); (Get-FileHash payload.bin).Hash
  ```
- **絵**: 中間調の多い画像（写真、グラデーション）。黒・白ベタが多いと、容量がほとんど取れません。
- **書き込みソフト**: 本アプリの「書き込み」ページ（実験的）、または ImgBurn、`growisofs`（DVD）、`wodim`/`cdrecord`（CD）。ISO を **そのまま、データとして、ディスクを閉じて** 書きます。マルチセッションにしてはいけません。

## 段階 A: 読み出しの確認（埋め込みなし）— H1

埋め込みなしの普通のディスクを書き、読み戻して、生成したイメージと比べます。

### CD

```bash
mkdir files && echo hello > files/readme.txt
disc-image-studio cd-generate --input picture.png --data-dir files --sectors 60000 \
  --tone-dither 0.75 --tone-gamma true --output cd-plain.iso
```

### DVD

```bash
disc-image-studio solve --image picture.png --iso-output dvd-plain.iso --total-sectors 262144 \
  --inner-radius-mm 24 --outer-radius-mm 58 --tone-dither 0.75 --tone-gamma true --data-dir files
```

`--data-dir` を付けるのは、Windows や macOS がディスクを認識できるようにするためです。ファイルシステムのない DVD は、Linux でしか生セクタで読めません。

### 書き込み → 読み出し → 比較

1. 上の ISO を **最低速度に近い速度** で書き込み、ディスクを閉じます。
2. 読み出します。LBA 0 から 2048 バイト/セクタで、全体を 1 本のイメージにします。読めないセクタはゼロで埋めます。
   ```bash
   tools/carrier-verify/read-disc.sh /dev/sr0 readback.iso                 # Linux
   tools/carrier-verify/read-disc.sh /dev/rdisk4 readback.iso              # macOS (先に diskutil unmountDisk /dev/disk4)
   ```
   ```powershell
   .\tools\carrier-verify\read-disc.ps1 -Drive E -Output readback.iso      # Windows、管理者権限の PowerShell
   ```
3. 比べます。
   ```bash
   disc-image-studio compare --expected cd-plain.iso --actual readback.iso
   ```

**合格**: `identical` が 100%（終了コード 0）。読み戻しのほうが長いのは問題ありません（リードアウト側の余り）。

**不合格のとき**は、`compare` の出力を見て切り分けます。

| 出力 | 意味 | 次の一手 |
|---|---|---|
| 差異のセクタがすべて「all zero」 | ドライブが読めなかったセクタ。データが変わったのではない | 書き込み速度を下げる、別メディア、別ドライブで読む、ディスクの汚れを確認 |
| 差異が末尾の数セクタだけ | ディスク末尾（リードアウト付近）の読み出し | `--to-lba` で末尾を除いて再比較。描画領域が合格なら問題なし |
| ゼロではないデータが大量に違う | **ドライブが別のバイトを返している**。H1 が崩れている（通常は起こりません） | 最初に違うバイトの `expected` と `read` を記録して報告 |
| 先頭から全部違う | LBA 0 の位置がずれている（途中から読んだ、トラックが違う） | 読み出し方を見直す。マルチセッションになっていないか |

## 段階 B: 埋め込みの確認 — H2

段階 A が合格したメディア・ドライブで進みます。

### 1. 容量を測り、入ることを確かめる

```bash
disc-image-studio cd-generate --input picture.png --data-dir files --sectors 60000 \
  --tone-dither 0.75 --tone-gamma true --capacity-only true --embed-file payload.bin
# DVD: solve ... --capacity-only true --embed-file payload.bin
```

`fits:` と出ることを確認します。測定は ISO を書きません。

### 2. 生成して書き込む

```bash
disc-image-studio cd-generate --input picture.png --data-dir files --sectors 60000 \
  --tone-dither 0.75 --tone-gamma true --output cd-embedded.iso --embed-file payload.bin
```

DVD は `solve ... --iso-output dvd-embedded.iso --embed-file payload.bin` です。生成の最後に `bit carrier: … stored` と出て、終了コード 0 であることを確認し、書き込みます。

### 3. 読み出して取り出す

```bash
tools/carrier-verify/read-disc.sh /dev/sr0 readback.iso
disc-image-studio compare --expected cd-embedded.iso --actual readback.iso      # まず H1 を再確認
disc-image-studio cd-extract --iso readback.iso --output recovered.bin          # DVD は extract
sha256sum recovered.bin payload.bin                                              # 同じ値になるはず
```

**合格**: `CRC-32 verified` と出て、`recovered.bin` の SHA-256 が `payload.bin` と一致する。

**不合格のとき**は、`extract` の診断を見ます。

| 出力 | 意味 | 次の一手 |
|---|---|---|
| `no carrier stream found`、かつ `compare` は 100% 一致 | **DVD**: ディスクの LBA 0 が PSN 0x30000 でない可能性（`extract` は既定でそう仮定して明暗を判定します）。**CD**、または DVD で PSN が合っているなら、符号化または抽出の不具合 | DVD は `--psn-offset` を変えて試す。それでも出なければ、出力一式を報告（下の「報告してほしいもの」） |
| `no carrier stream found`、`compare` で差異あり | 読み出しが壊れている | 段階 A の表へ |
| `all-zero (unread) sectors … N` | ドライブが読めなかったセクタ。その範囲の LBA が出ます | 再読み出し、別ドライブ。**データが書けていないのではなく読めていない** |
| `frames that should carry data but do not: N (LBA …)` | その範囲のフレームがデコードできない（読み出し誤り、またはバイトが違う） | `compare` で同じ LBA を見る。ゼロでない違いなら H1 の問題 |
| `stream is incomplete` で欠けが少量 | 一部のフレームを失った | 欠けた範囲は診断の LBA と一致するはず |

> 診断の「疑わしいフレーム」は、絵の透明な部分（アルファで除外した領域）も拾うことがあります。ファイルが完全に取り出せていれば問題ありません。

## 段階 C: 見た目の確認 — H3

**これがこの機能の核心です。** 並べ替えが物理的な見た目に出ないことを、実物で確かめます。

実ディスクの幾何（物理的な半径・ピッチ）に合わせるため、**満杯サイズ**で作ります。小さいディスクでは、描画が実際の半径とずれて、見た目の比較ができません。

- CD: `--sectors 359849`（既定。メディアの容量に合わせる）
- DVD: プロジェクトの設定、または [DISC_PRESETS.md](DISC_PRESETS.md) のプリセット値

見た目への影響が最も出やすい条件で比べるため、**容量の 80〜90% を埋めるファイル** で埋め込みます。ほぼ全てのグループが並べ替えられます。

```bash
# 容量を測って、その 9 割のファイルを作る
disc-image-studio cd-generate --input picture.png --data-dir files --sectors 359849 \
  --tone-dither 0.75 --tone-gamma true --capacity-only true     # "at most N bytes" を読む
head -c <Nの90%> /dev/urandom > big.bin
```

次の 2 枚を、**同じ画像・同じ設定・同じメディア** で作ります。

- ディスク P: 埋め込みなし（普通の絵）
- ディスク Q: 埋め込みあり（`--embed-file big.bin`）

撮影・観察の条件をそろえます。

- 同じ照明、同じ角度、同じ距離で、2 枚を並べて撮る（斜光の下で、虹色の描画が見える角度）。
- 全体と、拡大（中間調の領域）の両方。
- 読み出しシミュレーション（`disc-image-studio simulate` / `cd-preview-track`）の出力と見比べて、どちらが実物に近いかも記録する。

**合格**: 2 枚を並べて、通常の観察距離で違いが分からない。拡大して粒状感が少し違って見えるのは、記録して構いません（ディザの粒が変わるため）。

**不合格のとき**: 違いの種類を記録します。
- 全体の明るさが違う → 明バイトの数が保たれていない可能性。報告してください（イメージ上は保たれている検証済みなので、物理的な要因です）。
- 粒状感・ざらつきだけが違う → 「見た目が変わらない」の範囲をどこまで許すかの判断です。容量を減らす設定（中間調だけを使う、グループを小さくする）を検討します。

さらに段階 B の取り出しも、このディスク Q で行ってください（満杯近いサイズでも取り出せること）。

## 段階 D: 条件を変えて繰り返す — H4

| 変える条件 | 例 |
|---|---|
| ドライブ | 書き込みと読み出しで別のドライブ |
| メディア | メーカー違い、CD-R と CD-RW、DVD-R と DVD+R |
| 書き込み速度 | 最低速度と中速 |
| 書き込み経路 | 本アプリ（IMAPI）と ImgBurn / growisofs / cdrecord |

段階 A と B だけを繰り返せば十分です（段階 C は、条件を変えて 1〜2 回）。

## 結果の記録

次の表に書き込んで、結果とともに共有してください。

| 項目 | 記録 |
|---|---|
| 日付 | |
| 種類 | CD / DVD |
| メディア（メーカー・型番・速度） | |
| 書き込みドライブ（型番・ファームウェア） | |
| 読み出しドライブ | |
| 書き込み経路・速度 | |
| 絵・設定（セクタ数、半径、トーン） | |
| 埋め込みファイルのサイズ / 容量（測定値） | |
| 段階 A `compare` | identical …% |
| 段階 B `extract` | 成功 / 失敗、SHA-256 一致 |
| 段階 C 見た目 | 合格 / 不合格、所見、写真 |
| 備考 | |

### 報告してほしいもの

不合格だったときは、次を添えてください（原因の特定に必要です）。

1. `compare` の出力全体
2. `extract` / `cd-extract` の出力全体（診断の行を含む）
3. 生成時の `*.iso.json`（DVD）、または生成コマンドの出力
4. 使ったコマンドの全文（引数を含む）
5. 段階 C は、2 枚を並べた写真

## 注意

- 書き込みは **実験的** です。中断や停電でメディアが使えなくなります。
- ファイルは暗号化されません。ディスクを読めた人は誰でも取り出せます。
- この手順書の `read-disc.ps1`（Windows）は、開発環境にないため動作確認していません。動かなかったら、ISO として読み出せる別の方法（ImgBurn の「ディスクからイメージファイルを作成」など）で代えて構いません。ただし、**LBA 0 から 2048 バイト/セクタ** であることを確かめてください。
