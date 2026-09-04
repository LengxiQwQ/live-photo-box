using System;

namespace LivePhotoBox.Services;

/// <summary>
/// Thrown when an operation is requested that has not yet been implemented in the
/// Rebuilt Native pipeline.
/// </summary>
public sealed class RebuiltPipelineNotReadyException : InvalidOperationException
{
    public string Operation { get; }

    public RebuiltPipelineNotReadyException(string operation)
        : base($"'{operation}' is not implemented yet in the Rebuilt Native pipeline.")
    {
        Operation = operation;
    }
}
