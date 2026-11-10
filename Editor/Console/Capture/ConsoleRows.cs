using System;

namespace Orbiters.Logger.Editor
{
    /// <summary>The rows of a console being read (Unity's, or a test's).</summary>
    internal interface IConsoleRows : IDisposable
    {
        int Count { get; }

        /// <summary>The first <paramref name="lines"/> lines of a row, with "[hh:mm:ss] " when timestamps are on.</summary>
        string Lines(int row, int lines, out int mode);

        bool TryRead(int row, out UnityConsoleRow result);
    }

    /// <summary>Where <see cref="ConsoleHistory"/> reads: Unity's console, or rows a test gives.</summary>
    internal interface IConsoleSource
    {
        bool Available { get; }

        /// <summary>The rows, or null when they can't be read now.</summary>
        IConsoleRows Open();

        /// <summary>Makes every entry a row, with timestamps; true when settings changed (see UnityConsole.ShowAll).</summary>
        bool TakeAll();

        void ReleaseAll();

        bool WindowMayShow { get; }
    }

    /// <summary>Unity's console.</summary>
    internal sealed class UnityConsoleSource : IConsoleSource
    {
        public static readonly UnityConsoleSource Instance = new UnityConsoleSource();

        public bool Available => UnityConsole.Available;

        public IConsoleRows Open() => UnityConsole.Reader.Open();

        public bool TakeAll() => UnityConsole.ShowAll.Take(timestamps: true);

        public void ReleaseAll() => UnityConsole.ShowAll.Release();

        public bool WindowMayShow => UnityConsole.ConsoleWindowMayShow;
    }
}
