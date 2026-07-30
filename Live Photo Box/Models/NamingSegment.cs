using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;

namespace LivePhotoBox.Models
{
    /// <summary>命名片段类型 — 定义文件命名模板中一个构成单元的类型</summary>
    public enum NamingSegmentType
    {
        /// <summary>{name} — 原文件名（不含扩展名）</summary>
        OriginalName,
        /// <summary>自定义固定文字</summary>
        Literal,
        /// <summary>{date} — 当前系统日期</summary>
        Date,
        /// <summary>{time} — 当前系统时间</summary>
        Time,
        /// <summary>{exif_date} — 照片拍摄日期</summary>
        ExifDate,
        /// <summary>{exif_time} — 照片拍摄时间</summary>
        ExifTime,
        /// <summary>{counter} — 递增计数器</summary>
        Counter,
        /// <summary>{protocol} — 协议后缀</summary>
        Protocol,
    }

    /// <summary>命名片段 — 输出文件名模板的一个构成单元，支持在 ListView 中拖拽排序</summary>
    public partial class NamingSegment : ObservableObject
    {
        [ObservableProperty]
        private NamingSegmentType _type;

        /// <summary>格式字符串：Literal → 文字内容；Date/Time → DateTime 格式；Counter → "D3" / "D2" 等</summary>
        [ObservableProperty]
        private string _format = string.Empty;

        public NamingSegment()
        {
        }

        public NamingSegment(NamingSegmentType type, string format = "")
        {
            _type = type;
            _format = format;
        }

        // ── 展示属性（仅 get，用于 UI 绑定）──

        /// <summary>拖拽手柄图标</summary>
        public string DragHandleIcon => ""; // ≡ 图标

        /// <summary>片段图标（Glyph）</summary>
        public string DisplayIcon => Type switch
        {
            NamingSegmentType.OriginalName => "",
            NamingSegmentType.Literal => "",
            NamingSegmentType.Date => "",
            NamingSegmentType.Time => "",
            NamingSegmentType.ExifDate => "",
            NamingSegmentType.ExifTime => "",
            NamingSegmentType.Counter => "",
            NamingSegmentType.Protocol => "",
            _ => "",
        };

        /// <summary>片段主标签</summary>
        public string DisplayLabel => Type switch
        {
            NamingSegmentType.OriginalName => ResourceService.GetString("NamingSegment_OriginalName"),
            NamingSegmentType.Literal => !string.IsNullOrEmpty(Format) ? Format : ResourceService.GetString("NamingSegment_Literal"),
            NamingSegmentType.Date => $"{ResourceService.GetString("NamingSegment_SystemDate")}: {(!string.IsNullOrEmpty(Format) ? Format : "yyyyMMdd")}",
            NamingSegmentType.Time => $"{ResourceService.GetString("NamingSegment_SystemTime")}: {(!string.IsNullOrEmpty(Format) ? Format : "HHmmss")}",
            NamingSegmentType.ExifDate => $"{ResourceService.GetString("NamingSegment_CaptureDate")}: {(!string.IsNullOrEmpty(Format) ? Format : "yyyyMMdd")}",
            NamingSegmentType.ExifTime => $"{ResourceService.GetString("NamingSegment_CaptureTime")}: {(!string.IsNullOrEmpty(Format) ? Format : "HHmmss")}",
            NamingSegmentType.Counter => $"{ResourceService.GetString("NamingSegment_Counter")}: {(!string.IsNullOrEmpty(Format) ? Format : "001~")}",
            NamingSegmentType.Protocol => ResourceService.GetString("NamingSegment_Protocol"),
            _ => "?",
        };

        /// <summary>片段辅助说明</summary>
        public string DisplayDetail => Type switch
        {
            NamingSegmentType.OriginalName => ResourceService.GetString("NamingSegment_OriginalName_Desc"),
            NamingSegmentType.Literal => ResourceService.GetString("NamingSegment_Literal_Desc"),
            NamingSegmentType.Date => ResourceService.GetString("NamingSegment_Date_Desc"),
            NamingSegmentType.Time => ResourceService.GetString("NamingSegment_Time_Desc"),
            NamingSegmentType.ExifDate => ResourceService.GetString("NamingSegment_CaptureDate_Desc"),
            NamingSegmentType.ExifTime => ResourceService.GetString("NamingSegment_CaptureTime_Desc"),
            NamingSegmentType.Counter => ResourceService.GetString("NamingSegment_Counter_Desc"),
            NamingSegmentType.Protocol => ResourceService.GetString("NamingSegment_Protocol_Desc"),
            _ => "",
        };

        // ── 模板字符串转换 ──

        /// <summary>将当前片段转为模板字符串形式（如 {name}、{date:yyyyMMdd}）</summary>
        public string ToTemplateString() => Type switch
        {
            NamingSegmentType.OriginalName => "{name}",
            NamingSegmentType.Literal => Format,
            NamingSegmentType.Date => string.IsNullOrEmpty(Format) ? "{date}" : $"{{date:{Format}}}",
            NamingSegmentType.Time => string.IsNullOrEmpty(Format) ? "{time}" : $"{{time:{Format}}}",
            NamingSegmentType.ExifDate => string.IsNullOrEmpty(Format) ? "{exif_date}" : $"{{exif_date:{Format}}}",
            NamingSegmentType.ExifTime => string.IsNullOrEmpty(Format) ? "{exif_time}" : $"{{exif_time:{Format}}}",
            NamingSegmentType.Counter => string.IsNullOrEmpty(Format) ? "{counter}" : $"{{counter:{Format}}}",
            NamingSegmentType.Protocol => "{protocol}",
            _ => "",
        };
    }
}
