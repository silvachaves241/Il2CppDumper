#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Il2CppDumper
{
    internal static class CoreLogger
    {
        private static readonly AsyncLocal<TextWriter?> CurrentWriter = new();
        private static readonly AsyncLocal<List<string>?> CurrentWarnings = new();

        public static IDisposable Scope(TextWriter? writer, List<string>? warnings = null)
        {
            var prevWriter = CurrentWriter.Value;
            var prevWarnings = CurrentWarnings.Value;
            CurrentWriter.Value = writer;
            CurrentWarnings.Value = warnings;
            return new ScopeDisposer(prevWriter, prevWarnings);
        }

        public static void DetachWriter()
        {
            CurrentWriter.Value = null;
        }

        public static void Reset()
        {
            CurrentWriter.Value = null;
            CurrentWarnings.Value = null;
        }

        public static void WriteLine(string? message = "")
        {
            if (message != null && (message.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                                    message.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)))
            {
                CurrentWarnings.Value?.Add(message);
            }
            try
            {
                CurrentWriter.Value?.WriteLine(message);
            }
            catch (ObjectDisposedException)
            {
                CurrentWriter.Value = null;
            }
        }

        public static void WriteLine(string format, params object[] args)
        {
            var formatted = string.Format(format, args);
            WriteLine(formatted);
        }

        public static void WriteLine(object? obj)
        {
            WriteLine(obj?.ToString() ?? string.Empty);
        }

        public static void Write(string? message)
        {
            try
            {
                CurrentWriter.Value?.Write(message);
            }
            catch (ObjectDisposedException)
            {
                CurrentWriter.Value = null;
            }
        }

        public static void LogWarning(string message)
        {
            CurrentWarnings.Value?.Add(message);
            try
            {
                CurrentWriter.Value?.WriteLine(message);
            }
            catch (ObjectDisposedException)
            {
                CurrentWriter.Value = null;
            }
        }

        private sealed class ScopeDisposer : IDisposable
        {
            private readonly TextWriter? prevWriter;
            private readonly List<string>? prevWarnings;

            public ScopeDisposer(TextWriter? prevWriter, List<string>? prevWarnings)
            {
                this.prevWriter = prevWriter;
                this.prevWarnings = prevWarnings;
            }

            public void Dispose()
            {
                CurrentWriter.Value = prevWriter;
                CurrentWarnings.Value = prevWarnings;
            }
        }
    }
}
