#include "protocols/dual_file_target.h"
#include "foundation/portable_internal.h"
#include <algorithm>
#include "media/target_primary_observation.h"
#include "media/jpeg_gainmap_mpf.h"
#include "media/media_inspector.h"
#include "metadata/jpeg.h"

namespace lpb::target {
lpb_result apple_image(lpb_context*, const std::vector<uint8_t>&, bool, const std::vector<uint8_t>&, std::vector<uint8_t>&);
lpb_result apple_movie(lpb_context*, const std::vector<uint8_t>&, int64_t, const std::string&, std::vector<uint8_t>&);
lpb_result vivo_pair(lpb_context*, const std::vector<uint8_t>&, const std::vector<uint8_t>&, uint32_t, const std::string&, std::vector<uint8_t>&, std::vector<uint8_t>&);

// Remove only a fully observed, uniquely bound MiCamera pair occupying the
// complete primary suffix. Never infer a truncation boundary from markers.
static lpb_result prepare_dual_image(lpb_context* ctx,const std::vector<uint8_t>& input,bool heif,std::vector<uint8_t>& output) {
    output=input;if(heif)return LPB_RESULT_OK;
    media::target_primary_observation facts;std::string error;
    if(!media::observe_target_primary(input,facts,error)) {set_error(ctx,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;}
    if(facts.singleton_mpf&&!facts.singleton_size_matches) {
        media::jpeg_gainmap_mpf::jpeg_info index;
        if(!media::jpeg_gainmap_mpf::parse_jpeg(output,index)||!media::jpeg_gainmap_mpf::singleton_primary(index)||index.eoi_end>UINT32_MAX)
            return LPB_RESULT_INVALID_ARGUMENT;
        media::jpeg_gainmap_mpf::write_u32(output,index.mpf.entries[0].size_field_offset,
            static_cast<uint32_t>(index.eoi_end),index.mpf.little_endian);
    }
    std::vector<media::target_primary_auxiliary> assets;
    for(const auto& a:facts.auxiliaries)if(a.selector_kind==3)assets.push_back(a);
    if(assets.empty())return LPB_RESULT_OK;
    std::sort(assets.begin(),assets.end(),[](const auto& a,const auto& b){return a.range.offset<b.range.offset;});
    if(assets.size()!=2 || facts.auxiliaries.size()!=2 ||
       assets[0].semantic!="MiCameraSubimage" || assets[1].semantic!="MiCameraLensWatermark" ||
       assets[0].range.offset!=facts.encoding_boundary || assets[0].range.offset>input.size() ||
       assets[0].range.length>input.size()-assets[0].range.offset ||
       assets[1].range.offset!=assets[0].range.offset+assets[0].range.length ||
       assets[1].range.offset>input.size() || assets[1].range.length!=input.size()-assets[1].range.offset) {
        set_error(ctx,"MiCamera assets do not uniquely own the complete optional suffix.");return LPB_RESULT_UNSUPPORTED;
    }
    std::string xml;
    if(!media::remove_micamera_asset_description(facts.standard_xml,xml))return LPB_RESULT_UNSUPPORTED;
    std::vector<uint8_t> primary(input.begin(),input.begin()+static_cast<size_t>(facts.encoding_boundary)),rebuilt;
    if(metadata::replace_standard_jpeg_xmp_unchecked(primary,
        {reinterpret_cast<const uint8_t*>(xml.data()),xml.size()},rebuilt,error)!=LPB_RESULT_OK) {
        set_error(ctx,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
    }
    media::target_primary_observation checked;
    if(!media::observe_target_primary(rebuilt,checked,error) || !checked.auxiliaries.empty()) {
        set_error(ctx,"MiCamera omission left an invalid primary asset graph.");return LPB_RESULT_INVALID_ARGUMENT;
    }
    output=std::move(rebuilt);return LPB_RESULT_OK;
}

lpb_result serialize_dual_file(lpb_context* ctx, uint32_t target,
    const std::vector<uint8_t>& image, const std::vector<uint8_t>& video,
    bool heif, int64_t cover_us, uint32_t cover_frame, const std::string& id,
    std::vector<uint8_t>& image_out, std::vector<uint8_t>& video_out) {
    image_out.clear(); video_out.clear();
    const bool hex_id=std::all_of(id.begin(),id.end(),[](char c){return (c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F');});
    const bool uuid=id.size()==36&&id[8]=='-'&&id[13]=='-'&&id[18]=='-'&&id[23]=='-'&&
        std::count(id.begin(),id.end(),'-')==4&&std::all_of(id.begin(),id.end(),[](char c){return c=='-'||(c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F');});
    if (!ctx || image.empty() || video.empty() || cover_us < 0 ||
        (target!=7&&target!=8)||(target==7&&!uuid)||(target==8&&(!hex_id||id.size()!=28||heif))) {
        set_error(ctx,"Invalid dual-file target, input, cover, or pairing UUID."); return LPB_RESULT_INVALID_ARGUMENT;
    }
    try {
        std::vector<uint8_t> a,b,prepared; lpb_result result=prepare_dual_image(ctx,image,heif,prepared);
        if(result!=LPB_RESULT_OK)return result;
        if (target==7) {
            std::vector<uint8_t> note(70,0);
            const uint8_t header[]={ 'A','p','p','l','e',' ','i','O','S',0,0,1,'M','M',0,1,0,0x11,0,2,0,0,0,37,0,0,0,32,0,0,0,0 };
            std::copy(std::begin(header),std::end(header),note.begin());
            std::copy(id.begin(),id.end(),note.begin()+32);
            result=apple_image(ctx,prepared,heif,note,a);
            if(result==LPB_RESULT_OK) result=apple_movie(ctx,video,cover_us,id,b);
        } else result=vivo_pair(ctx,prepared,video,cover_frame,id,a,b);
        if(result!=LPB_RESULT_OK) return result;
        image_out=std::move(a); video_out=std::move(b); return LPB_RESULT_OK;
    } catch (...) { set_error(ctx,"Dual-file serialization failed."); return LPB_RESULT_INTERNAL_ERROR; }
}
}
