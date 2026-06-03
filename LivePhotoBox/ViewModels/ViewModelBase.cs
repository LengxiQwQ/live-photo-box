using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoBox.ViewModels
{
    public abstract partial class ViewModelBase : ObservableObject
    {
        public virtual string? PageStatusTag => null;

        public virtual string Status => string.Empty;
    }
}
