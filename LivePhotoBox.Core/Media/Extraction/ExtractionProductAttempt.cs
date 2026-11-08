using System;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Extraction;

internal sealed record ExtractionProductAttempt(ExtractedMediaBundle? Bundle, Exception? Failure)
{
    internal bool Succeeded => Bundle is not null && Failure is null;
    public static implicit operator ExtractionProductAttempt(ExtractedMediaBundle bundle) => new(bundle,null);
}
