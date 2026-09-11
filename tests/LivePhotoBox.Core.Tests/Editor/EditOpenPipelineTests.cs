using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Editor;

[Trait("Category", "EditOpenPipeline")]
public sealed class EditOpenPipelineTests : IDisposable
{
    private readonly string _tempDir;

    public EditOpenPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lpb_test_open_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private string CreateTempFile(string fileName, byte[]? content = null)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, content ?? new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        return path;
    }

    private sealed class MockSourceInspector : ISourceInspector
    {
        public Func<string, string?, CancellationToken, Task<SourceMediaFacts>>? OnInspect { get; set; }

        public Task<SourceMediaFacts> InspectAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default)
        {
            if (OnInspect != null)
                return OnInspect(primaryPath, secondaryPath, cancellationToken);

            string ext = Path.GetExtension(primaryPath);
            bool isVideo = ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".mov", StringComparison.OrdinalIgnoreCase);

            if (isVideo)
            {
                return Task.FromResult(new SourceMediaFacts
                {
                    Protocol = SourceProtocol.NonLive,
                    PrimaryImage = new ImageFacts { IsPresent = false },
                    MotionVideo = new VideoFacts
                    {
                        IsPresent = true,
                        SourceIndex = 0,
                        Width = 1920,
                        Height = 1080,
                        DurationSeconds = 5.0,
                        Fps = 30.0,
                        Container = VideoContainer.Mp4,
                        Codec = VideoCodec.H264
                    }
                });
            }

            return Task.FromResult(new SourceMediaFacts
            {
                Protocol = SourceProtocol.NonLive,
                PrimaryImage = new ImageFacts
                {
                    IsPresent = true,
                    Width = 4032,
                    Height = 3024,
                    Container = ImageContainer.Jpeg
                }
            });
        }
    }

    [Fact]
    public async Task OpenPhoto_SetsCurrentDocument()
    {
        var inspector = new MockSourceInspector();
        using var pipeline = new EditOpenPipeline(inspector);
        var photoPath = CreateTempFile("sample.jpg");

        bool result = await pipeline.OpenMediaAsync(photoPath);

        Assert.True(result);
        Assert.Equal(EditSessionState.Ready, pipeline.SessionState);
        Assert.NotNull(pipeline.CurrentDocument);
        Assert.Equal(photoPath, pipeline.CurrentDocument.PrimaryPath);
        Assert.Equal(EditMediaKind.Photo, pipeline.CurrentDocument.MediaKind);
        Assert.Equal((uint)4032, pipeline.CurrentDocument.Width);
        Assert.Equal((uint)3024, pipeline.CurrentDocument.Height);
    }

    [Fact]
    public async Task OpenVideo_SetsVideoDocument()
    {
        var inspector = new MockSourceInspector();
        using var pipeline = new EditOpenPipeline(inspector);
        var videoPath = CreateTempFile("clip.mp4");

        bool result = await pipeline.OpenMediaAsync(videoPath);

        Assert.True(result);
        Assert.Equal(EditSessionState.Ready, pipeline.SessionState);
        Assert.NotNull(pipeline.CurrentDocument);
        Assert.Equal(videoPath, pipeline.CurrentDocument.PrimaryPath);
        Assert.Equal(EditMediaKind.Video, pipeline.CurrentDocument.MediaKind);
        Assert.Equal(5.0, pipeline.CurrentDocument.DurationSeconds);
        Assert.Equal((uint)1920, pipeline.CurrentDocument.VideoWidth);
        Assert.Equal((uint)1080, pipeline.CurrentDocument.VideoHeight);
    }

    [Fact]
    public async Task NewOpen_InvalidatesPreviousOpen()
    {
        var pathA = CreateTempFile("A.jpg");
        var pathB = CreateTempFile("B.jpg");

        var tcsA = new TaskCompletionSource<bool>();

        var inspector = new MockSourceInspector
        {
            OnInspect = async (prim, sec, ct) =>
            {
                if (prim == pathA)
                {
                    await tcsA.Task;
                    ct.ThrowIfCancellationRequested();
                }
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.NonLive,
                    PrimaryImage = new ImageFacts { IsPresent = true, Width = 100, Height = 100 }
                };
            }
        };

        using var pipeline = new EditOpenPipeline(inspector);

        var taskA = pipeline.OpenMediaAsync(pathA);
        Assert.Equal(EditSessionState.Loading, pipeline.SessionState);

        var taskB = pipeline.OpenMediaAsync(pathB);
        bool resultB = await taskB;

        // 放行 A
        tcsA.TrySetResult(true);
        bool resultA = await taskA;

        Assert.False(resultA, "Old request A must be invalidated and return false.");
        Assert.True(resultB, "New request B must succeed.");
        Assert.Equal(EditSessionState.Ready, pipeline.SessionState);
        Assert.NotNull(pipeline.CurrentDocument);
        Assert.Equal(pathB, pipeline.CurrentDocument.PrimaryPath);
    }

    [Fact]
    public async Task FailedOpen_DoesNotOverwriteCurrentDocument()
    {
        var inspector = new MockSourceInspector();
        using var pipeline = new EditOpenPipeline(inspector);
        var pathA = CreateTempFile("valid.jpg");

        bool resultA = await pipeline.OpenMediaAsync(pathA);
        Assert.True(resultA);
        Assert.Equal(pathA, pipeline.CurrentDocument?.PrimaryPath);
        var docA = pipeline.CurrentDocument;

        // 尝试打开不存在的文件
        bool resultMissing = await pipeline.OpenMediaAsync(Path.Combine(_tempDir, "non_existent.jpg"));

        Assert.False(resultMissing);
        Assert.Same(docA, pipeline.CurrentDocument);
        Assert.Equal(EditSessionState.Ready, pipeline.SessionState);
    }

    [Fact]
    public async Task Rapid_A_B_A_FinalDocumentIsA()
    {
        var pathA = CreateTempFile("A.jpg");
        var pathB = CreateTempFile("B.jpg");

        var inspector = new MockSourceInspector
        {
            OnInspect = async (prim, sec, ct) =>
            {
                await Task.Delay(prim == pathA ? 30 : 10, ct);
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.NonLive,
                    PrimaryImage = new ImageFacts { IsPresent = true, Width = 800, Height = 600 }
                };
            }
        };

        using var pipeline = new EditOpenPipeline(inspector);

        var taskA1 = pipeline.OpenMediaAsync(pathA);
        var taskB = pipeline.OpenMediaAsync(pathB);
        var taskA2 = pipeline.OpenMediaAsync(pathA);

        await Task.WhenAll(taskA1, taskB, taskA2);

        Assert.Equal(EditSessionState.Ready, pipeline.SessionState);
        Assert.NotNull(pipeline.CurrentDocument);
        Assert.Equal(pathA, pipeline.CurrentDocument.PrimaryPath);
    }

    [Fact]
    public async Task DragDrop_MultipleFiles_OnlyOneBecomesCurrentDocument()
    {
        var path1 = CreateTempFile("first.jpg");
        var path2 = CreateTempFile("second.jpg");
        var path3 = CreateTempFile("third.mp4");

        var inspector = new MockSourceInspector();
        using var pipeline = new EditOpenPipeline(inspector);

        var dropResult = await pipeline.ProcessDroppedFilesAsync(new[] { path1, path2, path3 });

        Assert.True(dropResult.Succeeded);
        Assert.Equal(3, dropResult.Candidates.Count);
        Assert.NotNull(dropResult.OpenedRequest);
        Assert.Equal(path1, dropResult.OpenedRequest.PrimaryPath);

        // 确保且仅有首个主媒体建立为当前文档
        Assert.Equal(EditSessionState.Ready, pipeline.SessionState);
        Assert.NotNull(pipeline.CurrentDocument);
        Assert.Equal(path1, pipeline.CurrentDocument.PrimaryPath);
    }

    [Fact]
    public async Task ClearDuringOpen_PreventsLateReady()
    {
        var pathA = CreateTempFile("slow.jpg");
        var tcs = new TaskCompletionSource<bool>();

        var inspector = new MockSourceInspector
        {
            OnInspect = async (prim, sec, ct) =>
            {
                await tcs.Task;
                ct.ThrowIfCancellationRequested();
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.NonLive,
                    PrimaryImage = new ImageFacts { IsPresent = true, Width = 800, Height = 600 }
                };
            }
        };

        using var pipeline = new EditOpenPipeline(inspector);

        var openTask = pipeline.OpenMediaAsync(pathA);
        Assert.Equal(EditSessionState.Loading, pipeline.SessionState);

        // 中途关闭会话
        pipeline.Close();
        Assert.Equal(EditSessionState.Closed, pipeline.SessionState);
        Assert.Null(pipeline.CurrentDocument);

        // 放行后台异步
        tcs.TrySetResult(true);
        bool result = await openTask;

        Assert.False(result);
        Assert.Equal(EditSessionState.Closed, pipeline.SessionState);
        Assert.Null(pipeline.CurrentDocument);
    }

    [Fact]
    public async Task UnsupportedFile_FailsClosed()
    {
        var inspector = new MockSourceInspector();
        using var pipeline = new EditOpenPipeline(inspector);
        var badPath = CreateTempFile("document.txt", new byte[] { 1, 2, 3 });

        bool result = await pipeline.OpenMediaAsync(badPath);

        Assert.False(result);
        Assert.Equal(EditSessionState.Closed, pipeline.SessionState);
        Assert.Null(pipeline.CurrentDocument);
    }

    [Fact]
    public async Task SlowOpen_ThenClose_LateInspectionFinishes_CurrentDocumentNeverReappears_And_SessionNeverReturnsToReady()
    {
        var pathA = CreateTempFile("slow_A.jpg");
        var inspectionBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var inspector = new MockSourceInspector
        {
            OnInspect = async (prim, sec, ct) =>
            {
                await inspectionBarrier.Task;
                ct.ThrowIfCancellationRequested();
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.NonLive,
                    PrimaryImage = new ImageFacts { IsPresent = true, Width = 1920, Height = 1080 }
                };
            }
        };

        using var pipeline = new EditOpenPipeline(inspector);

        var docHistory = new List<EditDocument?>();
        var stateHistory = new List<EditSessionState>();

        pipeline.DocumentChanged += doc => docHistory.Add(doc);
        pipeline.SessionStateChanged += state => stateHistory.Add(state);

        // 1. 发起慢打开
        var openTask = pipeline.OpenMediaAsync(pathA);

        Assert.Equal(EditSessionState.Loading, pipeline.SessionState);
        Assert.NotNull(pipeline.CurrentDocument);
        Assert.Equal(EditSessionState.Loading, stateHistory.Last());
        Assert.Same(pipeline.CurrentDocument, docHistory.Last());

        // 2. Clear / Close 操作介入：先作废 generation 并 FailClosed
        pipeline.Close();

        Assert.Equal(EditSessionState.Closed, pipeline.SessionState);
        Assert.Null(pipeline.CurrentDocument);
        Assert.Equal(EditSessionState.Closed, stateHistory.Last());
        Assert.Null(docHistory.Last());

        int docEventsAtClose = docHistory.Count;
        int stateEventsAtClose = stateHistory.Count;

        // 3. 放行后台异步 inspection
        inspectionBarrier.TrySetResult(true);
        bool openResult = await openTask;

        // 4. 断言：旧请求已被作废，不得重新出现，Session 绝不能回 Ready
        Assert.False(openResult, "Cancelled/closed open task must return false.");
        Assert.Null(pipeline.CurrentDocument);
        Assert.Equal(EditSessionState.Closed, pipeline.SessionState);

        // 5. 断言：Close 后不得有任何迟到事件推送
        Assert.Equal(docEventsAtClose, docHistory.Count);
        Assert.Equal(stateEventsAtClose, stateHistory.Count);
        Assert.DoesNotContain(EditSessionState.Ready, stateHistory.Skip(stateEventsAtClose));
    }
}
