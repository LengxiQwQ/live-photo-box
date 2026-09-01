using System;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// Captures the product-wide processing branch once at the start of an operation.
/// Nested calls inherit the same session through <see cref="Current"/>.
/// </summary>
public sealed class ProcessingOperationSession
{
    internal ProcessingOperationSession(string operation, ProcessingBackendSettings settings)
    {
        Operation = operation;
        Mode = settings.Mode;
        Revision = settings.Revision;
    }

    internal ProcessingOperationSession(string operation, ProcessingOperationSession parent)
    {
        Operation = operation;
        Mode = parent.Mode;
        Revision = parent.Revision;
    }

    public string Operation { get; }
    public ProcessingPipelineMode Mode { get; }
    public long Revision { get; }
    public bool IsLegacy => Mode == ProcessingPipelineMode.Legacy;

    internal void EnsureLegacy()
    {
        if (!IsLegacy)
            throw new RebuiltPipelineNotReadyException(Operation);
    }
}

/// <summary>
/// The only product-wide processing router. It selects Legacy or the currently
/// empty Rebuilt branch before the supplied operation can create or mutate output.
/// </summary>
public static class ProcessingPipelineRouter
{
    private static readonly AsyncLocal<ProcessingOperationSession?> CurrentSlot = new();

    public static ProcessingOperationSession? Current => CurrentSlot.Value;

    public static ProcessingOperationSession Begin(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("An operation name is required.", nameof(operation));

        return new ProcessingOperationSession(operation, ProcessingBackendSettingsService.Load());
    }

    public static async Task RunAsync(string operation, Func<Task> legacyAction)
    {
        ArgumentNullException.ThrowIfNull(legacyAction);
        ProcessingOperationSession? inherited = Current;
        ProcessingOperationSession session = inherited != null
            ? new ProcessingOperationSession(string.IsNullOrWhiteSpace(operation) ? inherited.Operation : operation, inherited)
            : Begin(operation);

        session.EnsureLegacy();
        await RunInSessionAsync(session, legacyAction).ConfigureAwait(false);
    }

    public static async Task<T> RunAsync<T>(string operation, Func<Task<T>> legacyAction)
    {
        ArgumentNullException.ThrowIfNull(legacyAction);
        ProcessingOperationSession? inherited = Current;
        ProcessingOperationSession session = inherited != null
            ? new ProcessingOperationSession(string.IsNullOrWhiteSpace(operation) ? inherited.Operation : operation, inherited)
            : Begin(operation);

        session.EnsureLegacy();
        T result = default!;
        await RunInSessionAsync(session, async () => result = await legacyAction().ConfigureAwait(false))
            .ConfigureAwait(false);
        return result;
    }

    private static async Task RunInSessionAsync(ProcessingOperationSession session, Func<Task> action)
    {
        ProcessingOperationSession? previous = CurrentSlot.Value;
        CurrentSlot.Value = session;
        try { await action().ConfigureAwait(false); }
        finally { CurrentSlot.Value = previous; }
    }
}

