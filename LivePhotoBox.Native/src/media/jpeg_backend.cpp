#include "media/jpeg_backend.h"

#include "media/icc_profile_validation.h"

#include "media/jpeg_marker.h"

#include "media/jpeg_observation.h"

#include "media/image_exif_mapping.h"

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

bool map_dct_dimensions(std::span<const uint8_t> source,std::span<uint8_t> output,bool swap) noexcept {

    try {

        struct image { uint32_t width{},height{};size_t exif_offset{};std::vector<uint8_t> exif; } before,after;

        const auto observe=[](std::span<const uint8_t> bytes,image& facts) {

            lpb::memory_random_access input{bytes};uint64_t eoi{};bool sof=false,exif=false;

            const bool valid=lpb::media::walk_jpeg_markers(input.view(),eoi,[&](const lpb::media::jpeg_marker_range& marker) {

                const auto payload=bytes.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));

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

        if(!lpb::media::observe_exif(before.exif,orientation,exif,why,true)||orientation>1)return false;

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



} // namespace



namespace lpb::media {



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

    const int operation = to_turbo_transform(transform);

    if (operation < 0) { set_error(context, "Unsupported JPEG lossless transform operation."); return LPB_RESULT_INVALID_ARGUMENT; }



    windows_readonly_source source_lease;

    const fs::path source_path = utf8_to_path(input_path);

    if (!source_lease.open(source_path)) {

        set_error(context, "JPEG transform source could not be pinned read-only before orientation validation.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }



    // A physical transform plus a retained non-default EXIF Orientation would

    // double-rotate at display time. R4 intentionally fails closed until a

    // project-owned Exif rewrite can make that semantic update atomically.

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

        (observation.orientation != 0 && observation.orientation != 1)) {

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

    if(!map_dct_dimensions(source,std::span<uint8_t>(transformed,static_cast<size_t>(transformed_size)),swaps_dimensions)) {

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
