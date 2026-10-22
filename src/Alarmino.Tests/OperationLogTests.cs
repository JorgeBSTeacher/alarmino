using System.Text.RegularExpressions;
using Alarmino.Core.Services;
using Xunit;

namespace Alarmino.Tests;

// OperationLog keeps process-wide static state (log root + recent-lines buffer),
// and it is also exercised by other test classes. Running these tests in parallel
// with anything else that writes through the sink makes line counts and the
// recent-lines window race, so the whole class shares one serialized collection.
[CollectionDefinition("operation-log", DisableParallelization = true)]
public sealed class OperationLogCollection
{
}

[Collection("operation-log")]
public sealed class OperationLogTests
{
    [Fact]
    public void Write_creates_the_log_file_and_line_with_timestamp_and_source()
    {
        string dir = NewDir();
        try
        {
            OperationLog.ResetRoot(dir);
            OperationLog.Write("youtube", "prueba de escritura");

            string file = OperationLog.CurrentFilePath;
            Assert.True(File.Exists(file));

            string line = Assert.Single(File.ReadAllLines(file));
            Assert.EndsWith("prueba de escritura", line);
            Assert.Matches(new Regex(@"^\d{2}:\d{2}:\d{2}\.\d{3} \[youtube\] "), line);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Write_appends_successive_lines()
    {
        string dir = NewDir();
        try
        {
            OperationLog.ResetRoot(dir);
            OperationLog.Write("cache", "primera");
            OperationLog.Write("audio", "segunda");

            string[] lines = File.ReadAllLines(OperationLog.CurrentFilePath);

            Assert.Equal(2, lines.Length);
            Assert.Contains("[cache] primera", lines[0]);
            Assert.Contains("[audio] segunda", lines[1]);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Write_is_thread_safe_and_never_loses_lines()
    {
        string dir = NewDir();
        try
        {
            OperationLog.ResetRoot(dir);
            const int writers = 8;
            const int perWriter = 50;

            Parallel.For(0, writers, w =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    OperationLog.Write($"src{w}", $"mensaje {i}");
                }
            });

            Assert.Equal(writers * perWriter, File.ReadAllLines(OperationLog.CurrentFilePath).Length);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void RecentLines_keeps_only_the_last_lines_within_capacity()
    {
        string dir = NewDir();
        try
        {
            OperationLog.ResetRoot(dir);
            for (int i = 0; i < OperationLog.MaxRecentLines + 25; i++)
            {
                OperationLog.Write("test", $"linea {i}");
            }

            IReadOnlyList<string> recent = OperationLog.RecentLines;

            Assert.Equal(OperationLog.MaxRecentLines, recent.Count);
            Assert.EndsWith($"linea {OperationLog.MaxRecentLines + 25 - 1}", recent[^1]);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Write_never_throws_when_the_log_root_is_unusable()
    {
        string dir = NewDir();
        try
        {
            // Una ruta que no puede ser un directorio (un archivo ocupa su lugar).
            string blocker = Path.Combine(dir, "bloqueado");
            Directory.CreateDirectory(dir);
            File.WriteAllText(blocker, "x");
            OperationLog.ResetRoot(blocker);

            var exception = Record.Exception(() => OperationLog.Write("test", "mensaje"));

            Assert.Null(exception);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void LineWritten_is_raised_for_every_line()
    {
        string dir = NewDir();
        try
        {
            OperationLog.ResetRoot(dir);

            var seen = new List<string>();
            void Handler(string line) => seen.Add(line);

            OperationLog.LineWritten += Handler;
            try
            {
                OperationLog.Write("youtube", "pista descargada");
                OperationLog.Write("audio", "reproduciendo");
            }
            finally
            {
                OperationLog.LineWritten -= Handler;
            }

            Assert.Equal(2, seen.Count);
            Assert.EndsWith("[youtube] pista descargada", seen[0]);
            Assert.EndsWith("[audio] reproduciendo", seen[1]);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void A_failing_subscriber_does_not_break_the_log()
    {
        string dir = NewDir();
        try
        {
            OperationLog.ResetRoot(dir);

            void Broken(string line) => throw new InvalidOperationException("panel roto");

            OperationLog.LineWritten += Broken;
            try
            {
                var exception = Record.Exception(() => OperationLog.Write("test", "mensaje"));

                Assert.Null(exception);
            }
            finally
            {
                OperationLog.LineWritten -= Broken;
            }

            Assert.Single(File.ReadAllLines(OperationLog.CurrentFilePath));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "alarmino-log-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        OperationLog.ResetRoot(null);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
