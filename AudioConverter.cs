using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NVorbis;

namespace OggConverter
{
    public static class AudioConverter
    {
        public static async Task ConvertAsync(
            string sourcePath,
            string destPath,
            int    bitrate  = 0,
            CancellationToken ct = default,
            IProgress<double>? progress = null)
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (IsOpus(sourcePath))
                    ConvertOpusToWav(sourcePath, destPath, ct, progress);
                else
                    ConvertVorbisToWav(sourcePath, destPath, ct, progress);

                progress?.Report(1.0);
            }, ct);
        }

        // ── 检测 OGG Opus ────────────────────────────────────────────────────
        public static bool IsOpus(string path)
        {
            try
            {
                var buf = new byte[512];
                using var fs = File.OpenRead(path);
                int n = fs.Read(buf, 0, buf.Length);
                return System.Text.Encoding.ASCII.GetString(buf, 0, n).Contains("OpusHead");
            }
            catch { return false; }
        }

        // ── 查找 ffmpeg.exe ──────────────────────────────────────────────────
        public static string? FindFfmpeg()
        {
            // 1. 程序同目录
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            // 2. PATH 环境变量
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "")
                               .Split(Path.PathSeparator))
            {
                try
                {
                    var full = Path.Combine(d.Trim(), "ffmpeg.exe");
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        // ── OGG Vorbis → WAV（纯托管，NVorbis）──────────────────────────────
        private static void ConvertVorbisToWav(
            string src, string dest, CancellationToken ct, IProgress<double>? progress)
        {
            using var vorbis = new VorbisReader(src);
            int  rate  = vorbis.SampleRate;
            int  ch    = vorbis.Channels;
            long total = vorbis.TotalSamples;

            var fmt = new WaveFormat(rate, 16, ch);
            using var writer = new WaveFileWriter(dest, fmt);

            const int BUF = 8192;
            var fbuf  = new float[BUF * ch];
            var sbuf  = new short[BUF * ch];
            long read = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = vorbis.ReadSamples(fbuf, 0, fbuf.Length);
                if (n == 0) break;

                for (int i = 0; i < n; i++)
                    sbuf[i] = (short)(Math.Clamp(fbuf[i], -1f, 1f) * 32767f);

                var bytes = new byte[n * 2];
                Buffer.BlockCopy(sbuf, 0, bytes, 0, bytes.Length);
                writer.Write(bytes, 0, bytes.Length);

                read += n / ch;
                if (total > 0)
                    progress?.Report(Math.Min(0.99, (double)read / total));
            }
        }

        // ── OGG Opus → WAV（调用 FFmpeg 进程）───────────────────────────────
        private static void ConvertOpusToWav(
            string src, string dest, CancellationToken ct, IProgress<double>? progress)
        {
            string ffmpeg = FindFfmpeg()
                ?? throw new FileNotFoundException(
                    "检测到 OGG Opus 文件，需要 ffmpeg.exe 才能解码。\n\n" +
                    "请下载 FFmpeg：https://ffmpeg.org/download.html\n" +
                    "将 ffmpeg.exe 放到程序同目录（或加入系统 PATH），然后重试。");

            // 先获取时长用于进度计算
            double totalSec = GetDuration(ffmpeg, src);

            var args = $"-y -i \"{src}\" -acodec pcm_s16le -progress pipe:1 -nostats \"{dest}\"";

            var psi = new ProcessStartInfo
            {
                FileName               = ffmpeg,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new Exception("无法启动 FFmpeg 进程");

            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            });

            // 读取进度（stdout）
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (line.StartsWith("out_time_us=") &&
                    long.TryParse(line["out_time_us=".Length..], out long us) &&
                    totalSec > 0)
                {
                    progress?.Report(Math.Min(0.99, us / 1_000_000.0 / totalSec));
                }
            }

            // 读完 stderr 防止死锁
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            ct.ThrowIfCancellationRequested();

            if (proc.ExitCode != 0)
                throw new Exception($"FFmpeg 退出码 {proc.ExitCode}，转换失败");
        }

        // ── 获取音频时长（秒） ────────────────────────────────────────────────
        private static double GetDuration(string ffmpeg, string file)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = ffmpeg,
                    Arguments              = $"-i \"{file}\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi)!;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();

                var m = System.Text.RegularExpressions.Regex.Match(
                    err, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                if (m.Success)
                {
                    double h  = double.Parse(m.Groups[1].Value);
                    double mi = double.Parse(m.Groups[2].Value);
                    double s  = double.Parse(m.Groups[3].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    return h * 3600 + mi * 60 + s;
                }
            }
            catch { }
            return 0;
        }
    }
}
