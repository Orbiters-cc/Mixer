using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Contracts;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>
    /// Writes rendered frames on worker threads: <c>frames/NNNNNN.qoi</c> (top-down RGB) and
    /// <c>groundtruth/depth/NNNNNN.f16z</c>, and keeps, for every depth frame, the codes its symbols must decode to
    /// (the self-check compares them later). The queue is bounded so memory stays flat; buffers are pooled so the
    /// editor's garbage collector stays quiet.
    /// </summary>
    internal sealed class CaptureWriter : IDisposable
    {
        public readonly string Folder;
        public readonly int Width, Height;
        private readonly BlockingCollection<CaptureFrame> queue;
        private readonly Task[] workers;
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private readonly ConcurrentBag<byte[]> colourPool = new ConcurrentBag<byte[]>(), imagePool = new ConcurrentBag<byte[]>();
        private readonly ConcurrentBag<float[]> depthPool = new ConcurrentBag<float[]>();
        private readonly ConcurrentDictionary<int, ushort[]> expectedDepth = new ConcurrentDictionary<int, ushort[]>();
        private readonly DepthLayout depthLayout;
        private readonly bool hasDepthLayout;
        private int written, added, done;
        private long bytes;
        private volatile Exception error;

        public int Written => Volatile.Read(ref written);
        public long Bytes => Interlocked.Read(ref bytes);
        public Exception Error => error;
        /// <summary>Frames queued or being written.</summary>
        public int Pending => Volatile.Read(ref added) - Volatile.Read(ref done);
        public bool HasRoom => error == null && queue.Count < queue.BoundedCapacity;
        /// <summary>After <see cref="Complete"/>: every queued frame is written (or dropped after an error).</summary>
        public bool Finished => queue.IsAddingCompleted && Pending == 0;

        /// <param name="threads">Worker threads; keep about a quarter of the cores so the editor and the MCP bridge stay responsive.</param>
        public CaptureWriter(string folder, int width, int height, int threads)
        {
            Folder = folder;
            Width = width;
            Height = height;
            hasDepthLayout = DepthLayout.TryCreate(width, height, out depthLayout);
            long frameBytes = (long)width * height * 8;
            int capacity = (int)Math.Max(2, Math.Min(threads * 2 + 2, (384L << 20) / Math.Max(1, frameBytes)));
            queue = new BlockingCollection<CaptureFrame>(capacity);
            Directory.CreateDirectory(Path.Combine(folder, RawCapture.FramesDir));
            Directory.CreateDirectory(Path.Combine(folder, RawCapture.GroundTruthDepthDir));
            workers = new Task[Math.Max(1, threads)];
            for (int i = 0; i < workers.Length; i++) workers[i] = Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning);
        }

        public byte[] RentColour() => colourPool.TryTake(out var b) ? b : new byte[Width * Height * 4];

        public float[] RentDepth() => depthPool.TryTake(out var d) ? d : new float[Width * Height];

        /// <summary>Queues a frame; call only when <see cref="HasRoom"/> (it waits otherwise).</summary>
        public void Add(CaptureFrame frame)
        {
            if (error != null) throw new IOException("Writing the capture failed: " + error.Message, error);
            Interlocked.Increment(ref added);
            queue.Add(frame);
        }

        /// <summary>No more frames: the workers finish the queue and stop.</summary>
        public void Complete() => queue.CompleteAdding();

        /// <summary>Codes the depth symbols of frame <paramref name="index"/> must decode to (grid of the depth layout).</summary>
        public ushort[] ExpectedDepthCodes(int index) => expectedDepth.TryGetValue(index, out var codes) ? codes : null;

        private void Work()
        {
            try
            {
                foreach (var frame in queue.GetConsumingEnumerable(cancel.Token))
                {
                    try
                    {
                        if (error == null) Write(frame);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    finally
                    {
                        Interlocked.Increment(ref done);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void Write(CaptureFrame frame)
        {
            // Unity reads back bottom-up; frames are stored top-down.
            var top = imagePool.TryTake(out var pooled) ? pooled : new byte[Width * Height * 4];
            int row = Width * 4;
            for (int y = 0; y < Height; y++) Buffer.BlockCopy(frame.Colour, (Height - 1 - y) * row, top, y * row, row);
            var image = new ImageBuffer(Width, Height, top);
            long size = 0;
            string framePath = Path.Combine(Folder, RawCapture.FramesDir, RawCapture.FrameFile(frame.Index));
            using (var file = new FileStream(framePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                Qoi.Encode(image, file, 3);
                size += file.Length;
            }
            imagePool.Add(top);
            string depthPath = Path.Combine(Folder, RawCapture.GroundTruthDepthDir, RawCapture.GroundTruthDepthFile(frame.Index));
            using (var file = new FileStream(depthPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                GroundTruthDepth.Encode(frame.Depth, Width, Height, file, bottomUp: true);
                size += file.Length;
            }
            if (frame.DepthFrame && hasDepthLayout) expectedDepth[frame.Index] = ExpectedCodes(frame.Depth);
            colourPool.Add(frame.Colour);
            depthPool.Add(frame.Depth);
            frame.Colour = null;
            frame.Depth = null;
            Interlocked.Add(ref bytes, size);
            Interlocked.Increment(ref written);
        }

        /// <summary>The 16-bit codes of the true depth at every sample pixel of the used tiles (0 elsewhere).</summary>
        private ushort[] ExpectedCodes(float[] bottomUpDepth)
        {
            var layout = depthLayout;
            int gw = layout.GridWidth;
            var codes = new ushort[gw * layout.GridHeight];
            for (int gy = 0; gy < layout.GridHeight; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                if (!layout.TileUsed(gx / DepthLayout.SamplesX, gy / DepthLayout.SamplesY)) continue;
                layout.GridPixel(gx, gy, out int x, out int y);
                codes[gy * gw + gx] = DepthFrames.Quantize(bottomUpDepth[(Height - 1 - y) * Width + x]);
            }
            return codes;
        }

        /// <summary>Stops the workers without waiting for the queue (cancel); frames not written yet are dropped.</summary>
        public void Abort()
        {
            if (!queue.IsAddingCompleted) queue.CompleteAdding();
            cancel.Cancel();
        }

        public void Dispose()
        {
            Abort();
            try
            {
                Task.WaitAll(workers, 10000);
            }
            catch (AggregateException)
            {
            }
            queue.Dispose();
            cancel.Dispose();
        }
    }
}
