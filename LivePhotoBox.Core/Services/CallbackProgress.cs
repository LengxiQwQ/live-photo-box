using System;

namespace LivePhotoBox.Services;

// Internal stage adapters run inline; they never capture the caller's UI context.
internal sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
