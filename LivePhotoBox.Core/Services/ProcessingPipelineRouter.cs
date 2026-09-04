using System;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// Captures the operation session context at the start of a processing operation.
/// Nested calls inherit the same session through <see cref="ProcessingPipelineRouter.Current"/>.
/// </summary>
public sealed class ProcessingOperationSession
{
    internal ProcessingOperationSession(string operation, ProcessingBackendSettings settings)
    {
        Operation = operation;
        Revision = settings.Revision;
    }

    internal ProcessingOperationSession(string operation, ProcessingOperationSession parent)
    {
        Operation = operation;
        Revision = parent.Revision;
    }

    public string Operation { get; }
    public long Revision { get; }
}

/// <summary>
/// The processing operation boundary. All processing operations execute through
/// <see cref="RunAsync(string, Func{Task})"/> or <see cref="RunAsync{T}(string, Func{Task{T}})"/>
/// within the Rebuilt Native pipeline.
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

    public static async Task RunAsync(string operation, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ProcessingOperationSession? inherited = Current;
        ProcessingOperationSession session = inherited != null
            ? new ProcessingOperationSession(string.IsNullOrWhiteSpace(operation) ? inherited.Operation : operation, inherited)
            : Begin(operation);

        await RunInSessionAsync(session, action).ConfigureAwait(false);
    }

    public static async Task<T> RunAsync<T>(string operation, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ProcessingOperationSession? inherited = Current;
        ProcessingOperationSession session = inherited != null
            ? new ProcessingOperationSession(string.IsNullOrWhiteSpace(operation) ? inherited.Operation : operation, inherited)
            : Begin(operation);

        T result = default!;
        await RunInSessionAsync(session, async () => result = await action().ConfigureAwait(false))
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Alias for <see cref="RunAsync(string, Func{Task})"/> retained for semantic clarity at caller boundaries.
    /// </summary>
    public static Task RunRebuiltAsync(string operation, Func<Task> action) =>
        RunAsync(operation, action);

    /// <summary>
    /// Alias for <see cref="RunAsync{T}(string, Func{Task{T}})"/> retained for semantic clarity at caller boundaries.
    /// </summary>
    public static Task<T> RunRebuiltAsync<T>(string operation, Func<Task<T>> action) =>
        RunAsync(operation, action);

    private static async Task RunInSessionAsync(ProcessingOperationSession session, Func<Task> action)
    {
        ProcessingOperationSession? previous = CurrentSlot.Value;
        CurrentSlot.Value = session;
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            CurrentSlot.Value = previous;
        }
    }
}

