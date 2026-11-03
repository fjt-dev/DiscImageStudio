internal static class Check
{
    internal static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAILED: " + message);
    }

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"FAILED: {message} (expected {expected}, actual {actual})");
    }
}
