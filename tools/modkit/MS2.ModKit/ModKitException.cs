using System;

namespace MS2.ModKit
{
    /// <summary>Every failure the library reports itself. The message is meant to be shown to the user.</summary>
    public class ModKitException : Exception
    {
        public ModKitException(string message) : base(message) { }
        public ModKitException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>What a long operation is doing. Done and Total count the same unit (entries); Total is 0 if unknown.</summary>
    public sealed class ProgressInfo
    {
        public string Stage;
        public string Item;
        public long Done;
        public long Total;
    }

    internal static class ProgressUtil
    {
        internal static void Report(Action<ProgressInfo> cb, string stage, string item, long done, long total)
        {
            if (cb != null) cb(new ProgressInfo { Stage = stage, Item = item, Done = done, Total = total });
        }
    }
}
