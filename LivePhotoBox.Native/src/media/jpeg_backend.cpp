#include "media/jpeg_backend.h"

#include "media/icc_profile_validation.h"

#include "media/jpeg_marker.h"

#include "containers/heif_internal.h"
#include "metadata/exif.h"

#include "media/jpeg_observation.h"

#include "media/image_exif_mapping.h"
#include "media/xmp_observation.h"
#include "media/pixel_orientation.h"

#include "foundation/internal.h"

#include "platform/windows_filesystem.h"



#include <turbojpeg.h>

#include <algorithm>

#include <filesystem>

#include <limits>

#include <vector>



namespace fs = std::filesystem;



namespace {



constexpr uint64_t kMaxCompressedBytes = 512ull * 1024ull * 1024ull;

constexpr uint64_t kMaxPixels = 100ull * 1024ull * 1024ull;



#define LPB_STRINGIFY_IMPL(value) #value

#define LPB_STRINGIFY(value) LPB_STRINGIFY_IMPL(value)

constexpr const char kJpegBackendVersion[] = "libjpeg-turbo " LPB_STRINGIFY(TURBOJPEG_VERSION_NUMBER);



bool read_file_bounded(const fs::path& path, std::vector<unsigned char>& bytes) noexcept {

    windows_readonly_source input;

    return input.open(path) && input.read_all_bounded(2, kMaxCompressedBytes, bytes);

}



// Returns the byte immediately after the codestream EOI. It deliberately

// reaches EOI through the legal marker hierarchy instead of treating a string

// hit anywhere in a file as JPEG structure.

bool find_jpeg_eoi(std::span<const unsigned char> data, size_t& after_eoi) noexcept {

    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

    size_t pos = 2;

    bool in_scan = false;

    while (pos + 1 < data.size()) {

        if (!in_scan) {

            if (data[pos++] != 0xFF) return false;

            while (pos < data.size() && data[pos] == 0xFF) ++pos;

            if (pos >= data.size()) return false;

            const unsigned char marker = data[pos++];

            if (marker == 0xD9) { after_eoi = pos; return true; }

            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) continue;

            if (pos + 2 > data.size()) return false;

            const size_t length = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];

            if (length < 2 || length > data.size() - pos) return false;

            pos += length;

            if (marker == 0xDA) in_scan = true;

            continue;

        }

        if (data[pos++] != 0xFF) continue;

        if (pos >= data.size()) return false;

        const unsigned char marker = data[pos++];

        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;

        if (marker == 0xD9) { after_eoi = pos; return true; }

        // A progressive JPEG may have another legal marker segment between

        // scans. Re-enter the marker parser at its 0xFF byte.

        pos -= 2;

        in_scan = false;

    }

    return false;

}



// Reassembles the ICC APP2 sequence exactly as defined by the JPEG ICC

// convention. Color profile bytes are codec payload facts, not an excuse to

// reinterpret the pixels as sRGB.

bool extract_jpeg_icc_profile(std::span<const unsigned char> data, std::vector<uint8_t>& profile) noexcept {

    profile.clear();

    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

    const char signature[] = "ICC_PROFILE\0";

    std::vector<std::vector<uint8_t>> chunks;

    size_t p = 2;

    while (p + 4 <= data.size()) {

        if (data[p] != 0xFF) return false;

        while (p < data.size() && data[p] == 0xFF) ++p;

        if (p >= data.size()) return false;

        const uint8_t marker = data[p++];

        if (marker == 0xDA || marker == 0xD9) break;

        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;

        if (p + 2 > data.size()) return false;

        const size_t length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];

        if (length < 2 || length > data.size() - p) return false;

        if (marker == 0xE2 && length >= 16 && std::memcmp(data.data() + p + 2, signature, 12) == 0) {

            const uint8_t sequence = data[p + 14];

            const uint8_t count = data[p + 15];

            if (count == 0 || sequence == 0 || sequence > count) return false;

            if (chunks.empty()) chunks.resize(count);

            if (chunks.size() != count || !chunks[sequence - 1].empty()) return false;

            chunks[sequence - 1].assign(data.begin() + p + 16, data.begin() + p + length);

        }

        p += length;

    }

    if (chunks.empty()) return true;

    size_t total = 0;

    for (const auto& chunk : chunks) {

        if (chunk.empty() || chunk.size() > std::numeric_limits<size_t>::max() - total) return false;

        total += chunk.size();

    }

    if (total == 0 || total > 4u * 1024u * 1024u) return false;

    try { profile.reserve(total); }

    catch (...) { return false; }

    for (const auto& chunk : chunks) profile.insert(profile.end(), chunk.begin(), chunk.end());

    size_t declared_profile_size = 0;

    if (!lpb::media::detail::get_declared_icc_profile_size(profile, declared_profile_size)) {

        profile.clear();

        return false;

    }

    profile.resize(declared_profile_size);

    return true;

}



bool valid_rgb_layout(uint32_t width, uint32_t height, std::span<const uint8_t> rgb) noexcept {

    if (width == 0 || height == 0 || static_cast<uint64_t>(width) * height > kMaxPixels) return false;

    const uint64_t bytes = static_cast<uint64_t>(width) * height * 3;

    return bytes <= std::numeric_limits<size_t>::max() && rgb.size() == static_cast<size_t>(bytes);

}



// This primitive preserves complete ordinary markers. A physical DCT transform

// must also keep active EXIF dimensions consistent with the actual SOF raster.

// It does not grant a complete metadata/thumbnail mapping proof to old callers.

// Only documented, segment-owned preview signatures grant deletion authority.
// Unknown APP markers, MPF images, MakerNotes and trailers are never inferred.
bool preview_segment(uint8_t marker, std::span<const uint8_t> payload, size_t& jpeg_offset) {
    jpeg_offset = 0;
    const auto starts = [&](const char* text, size_t size) {
        return payload.size() >= size && std::memcmp(payload.data(), text, size) == 0;
    };
    if (marker == 0xe2) {
        if (starts("QVGA\0", 5)) { jpeg_offset = 5; return true; }
        if (starts("BGTH", 4)) { jpeg_offset = 4; return true; }
        if (starts("XMTH", 4)) { jpeg_offset = 9; return true; }
        return starts("\xff\xd8\xff", 3);
    }
    return marker == 0xe3 && starts("\xff\xd8\xff\xdb", 4);
}

bool strip_jpeg_previews(std::vector<uint8_t>& bytes, bool& changed) {
    changed = false;
    struct edit { size_t begin{}, end{}; std::vector<uint8_t> replacement; };
    std::vector<edit> edits;
    lpb::memory_random_access source{bytes}; uint64_t eoi{};
    const bool valid = lpb::media::walk_jpeg_markers(source.view(), eoi,
        [&](const lpb::media::jpeg_marker_range& marker) {
            const auto payload = std::span<const uint8_t>(bytes).subspan(
                static_cast<size_t>(marker.payload), static_cast<size_t>(marker.length));
            size_t offset{}; bool remove = preview_segment(marker.marker, payload, offset);
            if (marker.marker == 0xe0 && payload.size() >= 5 && !std::memcmp(payload.data(), "JFXX\0", 5)) {
                if (payload.size() < 6) return false;
                if (payload[5] == 0x10) { offset = 6; remove = true; }
                else if (payload[5] == 0x11 || payload[5] == 0x13) {
                    if (payload.size() < 8 || !payload[6] || !payload[7]) return false;
                    const size_t pixels = size_t(payload[6]) * payload[7];
                    if (payload.size() != 8 + (payload[5] == 0x11 ? 768 + pixels : 3 * pixels)) return false;
                    edits.push_back({static_cast<size_t>(marker.start), static_cast<size_t>(marker.payload + marker.length), {}});
                    return true;
                } else return false;
            }
            if (remove) {
                if (offset >= payload.size()) return false;
                lpb::memory_random_access embedded{payload.subspan(offset)};
                lpb::media::jpeg_observation observed; lpb::media::jpeg_observation_failure why{}; uint64_t bad{};
                if (!lpb::media::observe_jpeg(embedded.view(), observed, why, bad, {true,true,false,true}) ||
                    observed.eoi_end != payload.size() - offset) return false;
                edits.push_back({static_cast<size_t>(marker.start), static_cast<size_t>(marker.payload + marker.length), {}});
            }
            if (marker.marker == 0xe0 && payload.size() >= 5 && !std::memcmp(payload.data(), "JFIF\0", 5)) {
                if (payload.size() < 14) return false;
                const size_t pixels = size_t(payload[12]) * payload[13];
                if ((!payload[12]) != (!payload[13]) || payload.size() != 14 + 3 * pixels) return false;
                if (pixels) {
                    std::vector<uint8_t> replacement{0xff,0xe0,0,16};
                    replacement.insert(replacement.end(), payload.begin(), payload.begin() + 14);
                    replacement[16] = replacement[17] = 0;
                    edits.push_back({static_cast<size_t>(marker.start), static_cast<size_t>(marker.payload + marker.length), std::move(replacement)});
                }
            }
            return true;
        });
    if (!valid || eoi != bytes.size()) return false;
    for (auto edit = edits.rbegin(); edit != edits.rend(); ++edit) {
        bytes.erase(bytes.begin() + edit->begin, bytes.begin() + edit->end);
        bytes.insert(bytes.begin() + edit->begin, edit->replacement.begin(), edit->replacement.end());
    }
    changed = !edits.empty(); return true;
}

bool map_dct_dimensions(std::span<const uint8_t> source,std::span<uint8_t> output,bool swap,bool normalize_exif) noexcept {

    try {

        struct image { uint32_t width{},height{};size_t exif_offset{};std::vector<uint8_t> exif; } before,after;

        const auto observe=[normalize_exif](std::span<const uint8_t> bytes,image& facts) {

            lpb::memory_random_access input{bytes};uint64_t eoi{};bool sof=false,exif=false;

            const bool valid=lpb::media::walk_jpeg_markers(input.view(),eoi,[&](const lpb::media::jpeg_marker_range& marker) {

                const auto payload=bytes.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));

                size_t preview_offset{};
                if(normalize_exif && preview_segment(marker.marker, payload, preview_offset))return false;
                if(normalize_exif && marker.marker==0xe0 && payload.size()>=5) {
                    if(!std::memcmp(payload.data(),"JFXX\0",5))return false;
                    if(!std::memcmp(payload.data(),"JFIF\0",5) &&
                        (payload.size()<14 || payload[12]!=0 || payload[13]!=0))return false;
                }

                if(marker.marker==0xc0||marker.marker==0xc1||marker.marker==0xc2) {

                    if(sof||payload.size()<6)return false;sof=true;

                    facts.height=(uint32_t(payload[1])<<8)|payload[2];facts.width=(uint32_t(payload[3])<<8)|payload[4];

                }

                if(marker.marker==0xe1&&payload.size()>=6&&!std::memcmp(payload.data(),"Exif\0\0",6)) {

                    if(exif||marker.after_scan)return false;exif=true;facts.exif_offset=static_cast<size_t>(marker.payload);

                    facts.exif.assign(payload.begin(),payload.end());

                }

                return true;

            });

            return valid&&eoi==bytes.size()&&sof&&facts.width&&facts.height;

        };

        if(!observe(source,before)||!observe(output,after)||

            after.width!=(swap?before.height:before.width)||after.height!=(swap?before.width:before.height))return false;

        if(before.exif.empty())return after.exif.empty();

        uint32_t orientation{};lpb::media::exif_observation exif;lpb::media::jpeg_observation_failure why{};

        if(!lpb::media::observe_exif(before.exif,orientation,exif,why,true)||(!normalize_exif&&orientation>1))return false;

        auto mapped=before.exif;auto tiff=std::span<uint8_t>(mapped).subspan(6);const bool be=exif.big_endian;

        const auto read=[&](size_t at,size_t length) {uint32_t n{};for(size_t i=0;i<length;++i)n|=uint32_t(tiff[at+i])<<((be?length-i-1:i)*8);return n;};

        const auto patch=[&](uint32_t offset,bool root) {

            if(!offset||offset>tiff.size()||tiff.size()-offset<6)return false;

            const auto count=read(offset,2);if(count>(tiff.size()-offset-6)/12)return false;

            bool width=false,height=false;

            for(size_t i=0;i<count;++i) {

                const size_t at=offset+2+i*12;const auto tag=read(at,2);

                const bool horizontal=tag==(root?0x100U:0xa002U),vertical=tag==(root?0x101U:0xa003U);

                if(!horizontal&&!vertical)continue;

                if((horizontal&&width)||(vertical&&height))return false;width|=horizontal;height|=vertical;

                const auto type=read(at+2,2);if((type!=3&&type!=4)||read(at+4,4)!=1)return false;

                const size_t size=type==3?2:4;const auto old=read(at+8,size);

                if(old&&old!=(horizontal?before.width:before.height))return false;

                if(!old)continue;const auto value=horizontal?after.width:after.height;

                for(size_t j=0;j<size;++j)tiff[at+8+j]=static_cast<uint8_t>(value>>((be?size-j-1:j)*8));

            }

            return true;

        };

        if(normalize_exif) {
            // Do not rotate a primary while leaving an unverified preview behind.
            const size_t root=exif.ifd0_offset;
            if(root>tiff.size() || tiff.size()-root<6)return false;
            const auto count=read(root,2);
            if(count>(tiff.size()-root-6)/12 || read(root+2+count*12,4)!=0)return false;
            for(size_t i=0;i<count;++i) {
                const size_t entry=root+2+i*12;
                const auto tag=read(entry,2);
                if(tag==0x201 || tag==0x202)return false;
                if(tag!=0x112)continue;
                if(read(entry+2,2)!=3 || read(entry+4,4)!=1 || read(entry+8,2)!=orientation)return false;
                tiff[entry+8]=be?0:1;
                tiff[entry+9]=be?1:0;
            }
        }

        if(!patch(exif.ifd0_offset,true)||(exif.exif_ifd_offset&&!patch(exif.exif_ifd_offset,false)))return false;

        if(after.exif!=before.exif&&after.exif!=mapped)return false;

        if(after.exif_offset>output.size()||mapped.size()>output.size()-after.exif_offset)return false;

        std::copy(mapped.begin(),mapped.end(),output.begin()+static_cast<ptrdiff_t>(after.exif_offset));

        return true;

    }catch(...){return false;}

}



int to_turbo_transform(int32_t value) noexcept {

    switch (value) {

    case LPB_JPEG_TRANSFORM_ROTATE_90: return TJXOP_ROT90;

    case LPB_JPEG_TRANSFORM_ROTATE_180: return TJXOP_ROT180;

    case LPB_JPEG_TRANSFORM_ROTATE_270: return TJXOP_ROT270;

    case LPB_JPEG_TRANSFORM_FLIP_HORIZONTAL: return TJXOP_HFLIP;

    case LPB_JPEG_TRANSFORM_FLIP_VERTICAL: return TJXOP_VFLIP;

    case LPB_JPEG_TRANSFORM_TRANSPOSE: return TJXOP_TRANSPOSE;

    case LPB_JPEG_TRANSFORM_TRANSVERSE: return TJXOP_TRANSVERSE;

    default: return -1;

    }

}



// All offsets are TIFF-relative except the parser's absolute entry positions.
bool repair_exif(std::span<uint8_t> bytes, uint32_t wanted_orientation, bool strip,
                 uint32_t& changed) {
    using namespace lpb::media;
    uint32_t orientation{};
    exif_observation facts;
    jpeg_observation_failure failure{};
    if (!observe_exif(bytes, orientation, facts, failure, true, true)) return false;

    constexpr size_t tiff = 6;
    tiff_ifd root;
    if (!parse_ifd(bytes.data(), bytes.size(), tiff, facts.ifd0_offset, facts.big_endian, &root))
        return false;
    if (wanted_orientation && orientation != wanted_orientation) {
        for (const auto& entry : root.entries) {
            if (entry.tag != 0x112) continue;
            if (entry.type != 3 || entry.count != 1) return false;
            bytes[entry.absolute_pos + 8] = facts.big_endian ? 0 : static_cast<uint8_t>(wanted_orientation);
            bytes[entry.absolute_pos + 9] = facts.big_endian ? static_cast<uint8_t>(wanted_orientation) : 0;
            changed |= 2;
        }
    }

    if (!strip || !root.next_ifd_offset) return true;
    tiff_ifd thumbnail;
    if (!parse_ifd(bytes.data(), bytes.size(), tiff, root.next_ifd_offset, facts.big_endian, &thumbnail) ||
        thumbnail.next_ifd_offset) return false;

    uint32_t offset = 0, length = 0;
    for (const auto& entry : thumbnail.entries) {
        if (entry.tag == 0x201 || entry.tag == 0x202) {
            if (entry.type != 4 || entry.count != 1) return false;
            (entry.tag == 0x201 ? offset : length) = entry.value_offset;
            continue;
        }
        switch (entry.tag) {
        case 0x100: case 0x101: case 0x103: case 0x106: case 0x112: case 0x11a:
        case 0x11b: case 0x128: case 0x213: break;
        default: return false;
        }
    }
    if (!offset || !length || offset > bytes.size() - tiff ||
        length > bytes.size() - tiff - offset) return false;
    const uint64_t end = static_cast<uint64_t>(offset) + length;
    for (const auto& [start, finish] : facts.occupied_tiff_ranges)
        if (offset < finish && start < end) return false;
    lpb::memory_random_access reader{bytes.subspan(tiff + offset, length)};
    jpeg_observation jpeg;
    uint64_t bad{};
    if (!observe_jpeg(reader.view(), jpeg, failure, bad, {true, false, false, false}) ||
        jpeg.eoi_end != length) return false;

    const size_t next = root.absolute_pos + 2 + root.entries.size() * 12;
    std::fill_n(bytes.begin() + next, 4, uint8_t{});
    std::fill_n(bytes.begin() + thumbnail.absolute_pos, 6 + thumbnail.entries.size() * 12, uint8_t{});
    std::fill_n(bytes.begin() + tiff + offset, length, uint8_t{});
    changed |= 1;
    return true;
}

} // namespace



namespace lpb::media {



lpb_result normalize_repair_jpeg_bytes(lpb_context* context, std::span<const uint8_t> input,
    uint32_t orientation, bool strip, std::vector<uint8_t>& output, bool& reencoded) noexcept {
    output.clear(); reencoded = false;
    try {
        if (orientation > 8) return LPB_RESULT_UNSUPPORTED;
        const bool normalize = orientation != 0;
        lpb::memory_random_access memory{input}; jpeg_observation observed; jpeg_observation_failure why{}; uint64_t bad{};
        if (!observe_jpeg(memory.view(), observed, why, bad, {true,true,true,true}) || observed.eoi_end != input.size())
            return LPB_RESULT_UNSUPPORTED;
        std::vector<uint8_t> exif = observed.exif_payload;
        if (!exif.empty()) {
            uint32_t changes{};
            if (strip && !repair_exif(exif, 0, true, changes)) return LPB_RESULT_UNSUPPORTED;
            uint32_t old_orientation{}; exif_observation facts;
            if (!observe_exif(exif, old_orientation, facts, why, true, true)) return LPB_RESULT_UNSUPPORTED;
            const bool be = facts.big_endian;
            const auto get = [&](size_t pos, size_t n) { uint32_t value{}; for(size_t i=0;i<n;++i)value |= uint32_t(exif[pos+i])<<((be?n-i-1:i)*8); return value; };
            const auto put = [&](size_t pos, size_t n, uint32_t value) { for(size_t i=0;i<n;++i)exif[pos+i]=uint8_t(value>>((be?n-i-1:i)*8)); };
            const size_t root=6+facts.ifd0_offset, count=get(root,2), next=root+2+12*count;
            const uint32_t thumb_ifd=get(next,4);
            if (orientation > 1 && thumb_ifd) {
                tiff_ifd directory;
                if(!parse_ifd(exif.data(),exif.size(),6,thumb_ifd,be,&directory)||directory.next_ifd_offset)return LPB_RESULT_UNSUPPORTED;
                uint32_t offset{},length{};size_t offset_entry{},length_entry{};
                for(const auto& e:directory.entries) {
                    if(e.tag==0x201){if(e.type!=4||e.count!=1)return LPB_RESULT_UNSUPPORTED;offset=e.value_offset;offset_entry=e.absolute_pos+8;}
                    if(e.tag==0x202){if(e.type!=4||e.count!=1)return LPB_RESULT_UNSUPPORTED;length=e.value_offset;length_entry=e.absolute_pos+8;}
                }
                if(!offset||!length||6ULL+offset+length>exif.size())return LPB_RESULT_UNSUPPORTED;
                for(const auto& range:facts.occupied_tiff_ranges)
                    if(offset<range.second&&range.first<uint64_t(offset)+length)return LPB_RESULT_UNSUPPORTED;
                std::vector<uint8_t> thumbnail;bool thumbnail_reencoded{};
                if(normalize_repair_jpeg_bytes(context,std::span<const uint8_t>(exif).subspan(6+offset,length),orientation,true,thumbnail,thumbnail_reencoded)!=LPB_RESULT_OK)
                    return LPB_RESULT_UNSUPPORTED;
                reencoded |= thumbnail_reencoded;
                std::fill(exif.begin()+6+offset,exif.begin()+6+offset+length,uint8_t{});
                uint32_t new_offset=offset;
                if(thumbnail.size()<=length)std::copy(thumbnail.begin(),thumbnail.end(),exif.begin()+6+offset);
                else {
                    const size_t old_end=6+size_t(offset)+length;
                    const bool tail=std::all_of(exif.begin()+old_end,exif.end(),[](uint8_t value){return value==0;})&&
                        std::none_of(facts.occupied_tiff_ranges.begin(),facts.occupied_tiff_ranges.end(),[&](const auto& range){return range.second>offset;});
                    if(tail)exif.resize(6+offset);
                    else new_offset=static_cast<uint32_t>(exif.size()-6);
                    exif.insert(exif.end(),thumbnail.begin(),thumbnail.end());
                }
                put(offset_entry,4,new_offset);put(length_entry,4,static_cast<uint32_t>(thumbnail.size()));
                if(orientation>=5) {
                    const tiff_entry *width=nullptr,*height=nullptr;
                    for(const auto& e:directory.entries){if(e.tag==0x100)width=&e;if(e.tag==0x101)height=&e;}
                    if(width&&height){const size_t nw=width->type==3?2:4,nh=height->type==3?2:4;
                        const auto w=get(width->absolute_pos+8,nw),h=get(height->absolute_pos+8,nh);
                        put(width->absolute_pos+8,nw,h);put(height->absolute_pos+8,nh,w);}
                }
                for(const auto& e:directory.entries)if(e.tag==0x112)put(e.absolute_pos+8,2,1);
            }
            // The already normalized thumbnail no longer participates in the
            // primary mapping guard. Restore its exact directory link below.
            put(next,4,0);
            if(normalize)for(size_t i=0;i<count;++i){const size_t e=root+2+i*12;if(get(e,2)==0x112)put(e+8,2,orientation);}
            target_primary_observation mapping;mapping.width=observed.width;mapping.height=observed.height;mapping.orientation=orientation;
            if(normalize&&!map_image_exif_orientation(exif,mapping))return LPB_RESULT_UNSUPPORTED;
            put(next,4,thumb_ifd);
        }
        if(exif.size()>65533)return LPB_RESULT_UNSUPPORTED;
        std::vector<uint8_t> coded;
        if(orientation<=1) coded.assign(input.begin(),input.end());
        else {
            constexpr int ops[]{TJXOP_NONE,TJXOP_NONE,TJXOP_HFLIP,TJXOP_ROT180,TJXOP_VFLIP,TJXOP_TRANSPOSE,TJXOP_ROT90,TJXOP_TRANSVERSE,TJXOP_ROT270};
            tjhandle codec=tjInitTransform();unsigned char* transformed=nullptr;unsigned long size{};
            tjtransform transform{};transform.op=ops[orientation];transform.options=TJXOPT_PERFECT|TJXOPT_COPYNONE;
            const int status=codec?tjTransform(codec,input.data(),static_cast<unsigned long>(input.size()),1,&transformed,&size,&transform,TJFLAG_ACCURATEDCT):-1;
            if(codec)tjDestroy(codec);
            if(status==0&&transformed&&size)coded.assign(transformed,transformed+size);
            if(transformed)tjFree(transformed);
            if(coded.empty()) {
                jpeg_rgb_image decoded;
                if(decode_jpeg_rgb_bytes(context,input,decoded)!=LPB_RESULT_OK)return LPB_RESULT_UNSUPPORTED;
                pixel_surface surface;surface.width=decoded.width;surface.height=decoded.height;surface.channels=3;
                surface.signal_bit_depth=surface.storage_bit_depth=8;surface.stride=uint64_t(surface.width)*3;surface.pixels=std::move(decoded.pixels);
                if(!normalize_pixel_orientation(surface,orientation)||encode_rgb_jpeg_bytes(context,surface.width,surface.height,surface.pixels,92,{},coded)!=LPB_RESULT_OK)
                    return LPB_RESULT_UNSUPPORTED;
                reencoded=true;
            }
        }
        // Carry original metadata markers exactly, replacing only the primary
        // EXIF packet. The surrounding Samsung writer remaps XMP/MPF lengths.
        output={0xff,0xd8};uint64_t eoi{};
        if(!walk_jpeg_markers(memory.view(),eoi,[&](const jpeg_marker_range& m){
            if((m.marker<0xe0||m.marker>0xef)&&m.marker!=0xfe)return true;
            if(m.after_scan)return false;
            auto p=input.subspan(static_cast<size_t>(m.payload),static_cast<size_t>(m.length));
            if(m.marker==0xe1&&p.size()>=6&&!std::memcmp(p.data(),"Exif\0\0",6)) {
                output.insert(output.end(),{0xff,0xe1,uint8_t((exif.size()+2)>>8),uint8_t(exif.size()+2)});
                output.insert(output.end(),exif.begin(),exif.end());
            }else output.insert(output.end(),input.begin()+m.start,input.begin()+m.payload+m.length);
            return true;
        }))return LPB_RESULT_UNSUPPORTED;
        lpb::memory_random_access encoded{coded};std::vector<std::pair<size_t,size_t>> remove;
        if(!walk_jpeg_markers(encoded.view(),eoi,[&](const jpeg_marker_range& m){
            if((m.marker>=0xe0&&m.marker<=0xef)||m.marker==0xfe)remove.emplace_back(static_cast<size_t>(m.start),static_cast<size_t>(m.payload+m.length));return true;
        }))return LPB_RESULT_UNSUPPORTED;
        size_t start=2;for(const auto& [a,b]:remove){output.insert(output.end(),coded.begin()+start,coded.begin()+a);start=b;}
        output.insert(output.end(),coded.begin()+start,coded.end());
        return LPB_RESULT_OK;
    } catch (...) {set_error(context,"Samsung JPEG normalization could not retain the complete image metadata.");return LPB_RESULT_INTERNAL_ERROR;}
}

const char* jpeg_backend_version() noexcept {

    return kJpegBackendVersion;

}



lpb_result decode_jpeg_rgb_file(lpb_context* context, const char* input_path,

    jpeg_rgb_image& output) noexcept {

    output = {};

    if (!context || !input_path) {

        if (context) set_error(context, "JPEG decode requires a context and input path.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }



    std::vector<unsigned char> source;

    if (!read_file_bounded(utf8_to_path(input_path), source)) {

        set_error(context, "JPEG input is unavailable, too large for the configured safety limit, or malformed.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    return decode_jpeg_rgb_bytes(context, source, output);

}



lpb_result decode_jpeg_rgb_bytes(lpb_context* context, std::span<const uint8_t> input,

    jpeg_rgb_image& output) noexcept {

    output = {};

    if (!context || input.empty() || input.size() > kMaxCompressedBytes ||

        input.size() > std::numeric_limits<unsigned long>::max()) {

        if (context) set_error(context, "JPEG byte decode requires one bounded non-empty input buffer.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    const std::span<const unsigned char> source(

        reinterpret_cast<const unsigned char*>(input.data()), input.size());

    size_t after_eoi = 0;

    if (!find_jpeg_eoi(source, after_eoi)) {

        set_error(context, "JPEG decode requires a complete JPEG codestream.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    if (!extract_jpeg_icc_profile(source, output.icc_profile)) {

        set_error(context, "JPEG ICC profile markers are malformed or exceed the configured limit.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    // Decode only the structurally delimited codestream. A cross-container

    // caller must report such a conversion as partial preservation; it cannot

    // assert that protocol tails, GainMap carriers, or unknown APP payloads

    // have been reattached. DCT transforms take the stricter no-tail path.

    const auto codestream_size = static_cast<unsigned long>(after_eoi);



    tjhandle codec = tjInitDecompress();

    if (!codec) {

        set_error(context, "libjpeg-turbo decoder initialization failed.");

        return LPB_RESULT_INTERNAL_ERROR;

    }

    int width = 0;

    int height = 0;

    int subsampling = 0;

    int color_space = 0;

    if (tjDecompressHeader3(codec, source.data(), codestream_size,

            &width, &height, &subsampling, &color_space) != 0 || width <= 0 || height <= 0 ||

        static_cast<uint64_t>(width) * static_cast<uint64_t>(height) > kMaxPixels ||

        width > std::numeric_limits<int>::max() / 3) {

        tjDestroy(codec);

        set_error(context, "libjpeg-turbo rejected JPEG dimensions or header data.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }



    const uint64_t byte_count = static_cast<uint64_t>(width) * static_cast<uint64_t>(height) * 3;

    if (byte_count > std::numeric_limits<size_t>::max() || byte_count > std::numeric_limits<unsigned long>::max()) {

        tjDestroy(codec);

        set_error(context, "JPEG decode output exceeds the configured allocation limit.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    try {

        output.pixels.resize(static_cast<size_t>(byte_count));

    }

    catch (const std::bad_alloc&) {

        tjDestroy(codec);

        set_error(context, "JPEG decode could not allocate its bounded RGB output.");

        return LPB_RESULT_INTERNAL_ERROR;

    }



    const int decoded = tjDecompress2(codec, source.data(), codestream_size,

        output.pixels.data(), width, width * 3, height, TJPF_RGB, TJFLAG_ACCURATEDCT);

    tjDestroy(codec);

    if (decoded != 0) {

        output = {};

        set_error(context, "libjpeg-turbo JPEG decode failed.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    output.width = static_cast<uint32_t>(width);

    output.height = static_cast<uint32_t>(height);

    return LPB_RESULT_OK;

}



lpb_result encode_rgb_jpeg_bytes(lpb_context* context, uint32_t width, uint32_t height,

    std::span<const uint8_t> rgb, int32_t quality, std::span<const uint8_t> icc_profile,

    std::vector<uint8_t>& output) noexcept {

    output.clear();

    if (!context || !valid_rgb_layout(width, height, rgb)) {

        if (context) set_error(context, "JPEG byte encode received invalid dimensions or RGB buffer.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    constexpr size_t icc_segment_data_capacity = 65533u - 14u;

    if (icc_profile.size() > 4u * 1024u * 1024u ||

        (icc_profile.size() + icc_segment_data_capacity - 1) / icc_segment_data_capacity > 255u) {

        set_error(context, "JPEG ICC profile exceeds the project-owned APP2 representation limit.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    tjhandle codec = tjInitCompress();

    if (!codec) { set_error(context, "libjpeg-turbo encoder initialization failed."); return LPB_RESULT_INTERNAL_ERROR; }

    unsigned char* encoded = nullptr;

    unsigned long encoded_size = 0;

    const int clamped_quality = std::clamp(quality, 1, 100);

    const int result = tjCompress2(codec, const_cast<unsigned char*>(rgb.data()),

        static_cast<int>(width), 0, static_cast<int>(height), TJPF_RGB,

        &encoded, &encoded_size, TJSAMP_420, clamped_quality, TJFLAG_ACCURATEDCT);

    tjDestroy(codec);

    if (result != 0 || !encoded || encoded_size == 0) {

        if (encoded) tjFree(encoded);

        set_error(context, "libjpeg-turbo JPEG encoding failed.");

        return LPB_RESULT_INTERNAL_ERROR;

    }

    try {

        std::vector<uint8_t> jpeg;

        if (icc_profile.empty()) {

            jpeg.assign(encoded, encoded + static_cast<size_t>(encoded_size));

        } else {

            if (encoded_size < 2 || encoded[0] != 0xFF || encoded[1] != 0xD8) {

                tjFree(encoded);

                set_error(context, "JPEG encoder returned an invalid SOI marker before ICC insertion.");

                return LPB_RESULT_INTERNAL_ERROR;

            }

            const size_t chunk_count = (icc_profile.size() + icc_segment_data_capacity - 1) /

                icc_segment_data_capacity;

            jpeg.reserve(static_cast<size_t>(encoded_size) + icc_profile.size() + chunk_count * 18u);

            jpeg.insert(jpeg.end(), encoded, encoded + 2);

            constexpr uint8_t signature[] = { 'I','C','C','_','P','R','O','F','I','L','E',0 };

            size_t profile_offset = 0;

            for (size_t index = 0; index < chunk_count; ++index) {

                const size_t chunk_size = std::min(icc_segment_data_capacity, icc_profile.size() - profile_offset);

                const uint16_t marker_length = static_cast<uint16_t>(chunk_size + sizeof(signature) + 4u);

                jpeg.push_back(0xFF);

                jpeg.push_back(0xE2);

                jpeg.push_back(static_cast<uint8_t>(marker_length >> 8));

                jpeg.push_back(static_cast<uint8_t>(marker_length));

                jpeg.insert(jpeg.end(), std::begin(signature), std::end(signature));

                jpeg.push_back(static_cast<uint8_t>(index + 1));

                jpeg.push_back(static_cast<uint8_t>(chunk_count));

                jpeg.insert(jpeg.end(), icc_profile.begin() + static_cast<std::ptrdiff_t>(profile_offset),

                    icc_profile.begin() + static_cast<std::ptrdiff_t>(profile_offset + chunk_size));

                profile_offset += chunk_size;

            }

            jpeg.insert(jpeg.end(), encoded + 2, encoded + static_cast<size_t>(encoded_size));

        }

        output = std::move(jpeg);

    } catch (...) {

        output.clear();

    }

    tjFree(encoded);

    if (output.empty()) {

        output.clear();

        set_error(context, "Failed to assemble the libjpeg-turbo encoded JPEG bytes.");

        return LPB_RESULT_INTERNAL_ERROR;

    }

    return LPB_RESULT_OK;

}



lpb_result encode_rgb_jpeg_file(lpb_context* context, const char* output_path,

    uint32_t width, uint32_t height, std::span<const uint8_t> rgb, int32_t quality,

    std::span<const uint8_t> icc_profile,

    std::span<const uint8_t> exif_payload) noexcept {

    if (!context || !output_path) {

        if (context) set_error(context, "JPEG encode requires a context and output path.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    std::vector<uint8_t> jpeg;

    const lpb_result encoded = encode_rgb_jpeg_bytes(context, width, height, rgb,

        quality, icc_profile, jpeg);

    if (encoded != LPB_RESULT_OK) return encoded;



    if (!exif_payload.empty()) {

        std::vector<uint8_t> with_exif;

        if (!insert_exact_exif(jpeg, exif_payload, with_exif, 1)) {
            set_error(context, "Cannot inject the required EXIF payload; no JPEG was published.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        jpeg = std::move(with_exif);

    }

    const fs::path output_path_native = utf8_to_path(output_path);

    windows_owned_output output;

    if (!output.create(output_path_native, L"lpb-jpeg-encode") || !output.write_all(jpeg)) {

        static_cast<void>(abort_owned_output_and_report(context, output,

            "Failed to stage the libjpeg-turbo encoded JPEG."));

        return LPB_RESULT_INTERNAL_ERROR;

    }

    const lpb_result cancellation = lpb_context_check_cancelled(context);

    if (cancellation != LPB_RESULT_OK) {

        return abort_owned_output_and_report(context, output,

            "[Cancelled] JPEG encoding was cancelled before publication.")

            ? cancellation : LPB_RESULT_INTERNAL_ERROR;

    }

    const bool written = output.publish_registered_no_replace(

        context, LPB_ARTIFACT_PRIMARY_IMAGE, output_path_native);

    if (!written) {

        static_cast<void>(abort_owned_output_and_report(context, output,

            "Failed to publish the libjpeg-turbo encoded JPEG."));

        return LPB_RESULT_INTERNAL_ERROR;

    }

    return LPB_RESULT_OK;

}



lpb_result transform_jpeg_losslessly(lpb_context* context, const char* input_path,

    const char* output_path, int32_t transform) noexcept {

    if (!context || !input_path || !output_path || paths_alias(input_path, output_path)) {

        if (context) set_error(context, "JPEG lossless transform requires distinct input and output paths.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    const bool normalize_exif = transform == LPB_JPEG_TRANSFORM_NORMALIZE_EXIF;
    int operation = to_turbo_transform(transform);
    if (!normalize_exif && operation < 0) { set_error(context, "Unsupported JPEG lossless transform operation."); return LPB_RESULT_INVALID_ARGUMENT; }



    windows_readonly_source source_lease;

    const fs::path source_path = utf8_to_path(input_path);

    if (!source_lease.open(source_path)) {

        set_error(context, "JPEG transform source could not be pinned read-only before orientation validation.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }



    // A physical transform plus a retained non-default EXIF Orientation would

    // double-rotate at display time. Only the explicit EXIF normalization
    // operation may transform pixels and reset the authoritative tag together.

    lpb_preservation_observation observation{};

    observation.struct_size = sizeof(observation);

    const lpb_result observation_result = lpb_capture_preservation_observation(context, input_path,

        LPB_SOURCE_PROTOCOL_UNKNOWN, LPB_IMAGE_CONTAINER_JPEG, &observation);

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {

        set_error(context, "[Cancelled] JPEG orientation validation was cancelled.");

        return LPB_RESULT_CANCELLED;

    }

    if (observation_result != LPB_RESULT_OK ||

        (observation.flags & LPB_POBS_EXIF_PARSE_ERROR) != 0 ||

        (!normalize_exif && observation.orientation != 0 && observation.orientation != 1)) {

        set_error(context, "JPEG lossless transform refuses non-default or unverifiable EXIF Orientation to prevent double rotation.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }



    std::vector<unsigned char> source;

    if (!read_file_bounded(source_path, source) || !source_lease.verify_same_identity(source_path)) {

        set_error(context, "JPEG input is unavailable, too large for the configured safety limit, or malformed.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    size_t after_eoi = 0;

    if (!find_jpeg_eoi(source, after_eoi)) { set_error(context, "JPEG lossless transform requires a complete JPEG codestream."); return LPB_RESULT_INVALID_ARGUMENT; }

    if (after_eoi != source.size()) {

        set_error(context, "JPEG lossless transform refuses appended protocol/GainMap data; project-owned structural reassembly is required.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }



    if (normalize_exif) {
        // EXIF values describe the complete display transform, including mirrors.
        constexpr int operations[]{-1, -1, TJXOP_HFLIP, TJXOP_ROT180, TJXOP_VFLIP,
            TJXOP_TRANSPOSE, TJXOP_ROT90, TJXOP_TRANSVERSE, TJXOP_ROT270};
        lpb::memory_random_access input{source};
        jpeg_observation jpeg;
        jpeg_observation_failure failure{};
        uint64_t bad_offset{};
        if (!observe_jpeg(input.view(), jpeg, failure, bad_offset, {true, false, false, false}) ||
            jpeg.orientation < 2 || jpeg.orientation > 8 ||
            jpeg.mpf || observation.orientation != jpeg.orientation) {
            set_error(context, "JPEG repair requires a single unambiguous EXIF orientation without competing XMP or MPF metadata.");
            return LPB_RESULT_UNSUPPORTED;
        }
        if (!jpeg.xmp.empty()) {
            std::string mapped;
            xmp_observation_failure xml_failure{};
            if (!rewrite_neutral_image_xmp(jpeg.xmp, "image/jpeg", "image/jpeg",
                jpeg.orientation, jpeg.width, jpeg.height, mapped, xml_failure) || mapped != jpeg.xmp) {
                set_error(context, "JPEG rotation cannot retain XMP with unmapped active geometry or orientation.");
                return LPB_RESULT_UNSUPPORTED;
            }
        }
        operation = operations[jpeg.orientation];
    }

    tjhandle codec = tjInitTransform();

    if (!codec) { set_error(context, "libjpeg-turbo transform initialization failed."); return LPB_RESULT_INTERNAL_ERROR; }

    tjtransform xform{};

    xform.op = operation;

    xform.options = TJXOPT_PERFECT; // Reject MCU-unaligned inputs; never crop or re-encode silently.

    unsigned char* transformed = nullptr;

    unsigned long transformed_size = 0;

    const int result = tjTransform(codec, source.data(), static_cast<unsigned long>(source.size()), 1,

        &transformed, &transformed_size, &xform, TJFLAG_ACCURATEDCT);

    tjDestroy(codec);

    if (result != 0 || !transformed || transformed_size == 0) {

        if (transformed) tjFree(transformed);

        set_error(context, "libjpeg-turbo could not perform a perfect DCT-domain transform for this JPEG.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    const bool swaps_dimensions=operation==TJXOP_ROT90||operation==TJXOP_ROT270||operation==TJXOP_TRANSPOSE||operation==TJXOP_TRANSVERSE;

    if(!map_dct_dimensions(source,std::span<uint8_t>(transformed,static_cast<size_t>(transformed_size)),swaps_dimensions,normalize_exif)) {

        tjFree(transformed);

        set_error(context,"JPEG DCT output has inconsistent or unverifiable active EXIF dimensions; no output was published.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    windows_owned_output output;

    const fs::path destination = utf8_to_path(output_path);

    const bool staged = output.create(destination, L"lpb-jpeg-lossless") &&

        output.write_all(std::span<const uint8_t>(transformed, static_cast<size_t>(transformed_size)));

    tjFree(transformed);

    if (!staged) {

        static_cast<void>(abort_owned_output_and_report(context, output,

            "Failed to stage the losslessly transformed JPEG."));

        return LPB_RESULT_INTERNAL_ERROR;

    }

    const lpb_result cancellation = lpb_context_check_cancelled(context);

    if (cancellation != LPB_RESULT_OK) {

        return abort_owned_output_and_report(context, output,

            "[Cancelled] JPEG lossless transformation was cancelled before publication.")

            ? cancellation : LPB_RESULT_INTERNAL_ERROR;

    }

    const bool written = output.publish_registered_no_replace(

        context, LPB_ARTIFACT_PRIMARY_IMAGE, destination);

    if (!written) {

        static_cast<void>(abort_owned_output_and_report(context, output,

            "Failed to publish the losslessly transformed JPEG."));

        return LPB_RESULT_INTERNAL_ERROR;

    }

    return LPB_RESULT_OK;

}




} // namespace lpb::media


LPB_API lpb_result LPB_CALL lpb_repair_image_exif(
    lpb_context* context, const char* source_path, const char* output_path,
    uint32_t actions, uint32_t* changed)
{
    if (!context || !source_path || !changed || !actions || (actions & ~3u) ||
        (output_path && paths_alias(source_path, output_path))) return LPB_RESULT_INVALID_ARGUMENT;
    *changed = 0;
    const auto unsupported = [&](const char* message) {
        set_error(context, message);
        return LPB_RESULT_UNSUPPORTED;
    };
    try {
        windows_readonly_source lease;
        const auto source = utf8_to_path(source_path);
        std::vector<uint8_t> bytes;
        if (!lease.open(source) || !lease.read_all_bounded(4, kMaxCompressedBytes, bytes))
            return unsupported("Image repair input could not be pinned or read.");

        uint32_t actual{};
        if (bytes[0] == 0xff && bytes[1] == 0xd8) {
            lpb::memory_random_access input{bytes};
            lpb::media::jpeg_observation jpeg;
            lpb::media::jpeg_observation_failure failure{};
            uint64_t bad{};
            if (!lpb::media::observe_jpeg(input.view(), jpeg, failure, bad, {true, true, false, true}) ||
                jpeg.eoi_end != bytes.size())
                return unsupported("JPEG repair requires a complete standalone codestream without MPF or appended data.");
            if ((actions & 1) && !jpeg.exif_payload.empty() &&
                !repair_exif(std::span<uint8_t>(bytes).subspan(jpeg.exif_segment_end - jpeg.exif_payload.size(),
                    jpeg.exif_payload.size()), 0, true, actual))
                return unsupported("The EXIF thumbnail is not an independently owned JPEG thumbnail.");
            if (actions & 1) {
                bool previews{};
                if (!strip_jpeg_previews(bytes, previews))
                    return unsupported("JPEG preview ownership or complete embedded image could not be verified.");
                if (previews) actual |= 1;
            }
        } else {
            lpb::containers::heif_primary_properties primary;
            std::string error;
            if (!lpb::containers::observe_primary_item_properties(bytes, primary, error))
                return unsupported(error.c_str());
            if (!primary.exif_payload.empty()) {
                const auto wanted = (actions & 2) && primary.has_display_transform ? primary.orientation : 0;
                if (!repair_exif(primary.exif_payload, wanted, actions & 1, actual))
                    return unsupported("HEIF primary EXIF orientation or thumbnail ownership could not be verified.");
                if (actual && !lpb::containers::replace_primary_exif_payload(bytes, primary.exif_payload, error))
                    return unsupported(error.empty() ? "Could not verify primary EXIF extent replacement." : error.c_str());
            }
            if (actions & 1) {
                bool thumbnails{};
                if (!lpb::containers::strip_primary_thumbnails(bytes, thumbnails, error))
                    return unsupported(error.c_str());
                if (thumbnails) actual |= 1;
            }
        }

        const auto cancelled = lpb_context_check_cancelled(context);
        if (cancelled != LPB_RESULT_OK) return cancelled;
        if (!actual || !output_path) {
            *changed = actual;
            return LPB_RESULT_OK;
        }
        if (!lease.verify_same_identity(source)) return unsupported("Image repair source identity changed.");

        windows_owned_output output;
        const auto destination = utf8_to_path(output_path);
        if (!output.create(destination, L"lpb-repair-exif") || !output.write_all(bytes) ||
            lpb_context_check_cancelled(context) != LPB_RESULT_OK ||
            !output.publish_registered_no_replace(context, LPB_ARTIFACT_PRIMARY_IMAGE, destination)) {
            static_cast<void>(abort_owned_output_and_report(context, output, "Could not publish repaired EXIF output."));
            const auto status = lpb_context_check_cancelled(context);
            return status == LPB_RESULT_OK ? LPB_RESULT_INTERNAL_ERROR : status;
        }
        *changed = actual;
        return LPB_RESULT_OK;
    } catch (...) {
        set_error(context, "Unexpected failure while repairing primary EXIF metadata.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}
