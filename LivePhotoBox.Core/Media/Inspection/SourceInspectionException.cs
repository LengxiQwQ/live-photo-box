using System;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Media.Inspection;

public sealed class SourceInspectionException : Exception
{
    public SourceInspectionFailureCategory Category { get; }
    public SourceInspectionStage Stage { get; }
    public ulong Capability { get; }
    public string Reason { get; }

    public SourceInspectionException(SourceInspectionFailureCategory category,
        SourceInspectionStage stage, ulong capability, string message)
        : base(message)
    {
        Category = category;
        Stage = stage;
        Capability = capability;
        Reason = message;
    }

    public SourceInspectionException(SourceInspectionFailureCategory category,
        SourceInspectionStage stage, ulong capability, string message, Exception innerException)
        : base(message, innerException)
    {
        Category = category;
        Stage = stage;
        Capability = capability;
        Reason = message;
    }
}

public enum SourceInspectionFailureCategory { Unsupported, Ambiguous, Malformed, InvalidArgument, Io }
public enum SourceInspectionStage { Read, Container, Metadata, Protocol, Pairing }
