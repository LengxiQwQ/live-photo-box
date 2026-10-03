#ifndef NOMINMAX
#define NOMINMAX
#endif

#include "containers/isobmff.h"
#include "containers/isobmff_video_profile.h"
#include "media/video_presentation_timing.h"

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mftransform.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <span>
#include <string>
#include <utility>
#include <vector>

namespace {

template<class T>
class com_ptr {
public:
    com_ptr() = default;
    explicit com_ptr(T* value) noexcept : value_(value) {}
    ~com_ptr() { reset(); }
    com_ptr(const com_ptr&) = delete;
    com_ptr& operator=(const com_ptr&) = delete;
    com_ptr(com_ptr&& other) noexcept : value_(other.detach()) {}
    com_ptr& operator=(com_ptr&& other) noexcept {
        if (this != &other) {
            reset();
            value_ = other.detach();
        }
        return *this;
    }

    T* get() const noexcept { return value_; }
    T** put() noexcept {
        reset();
        return &value_;
    }
    T* detach() noexcept {
        T* detached = value_;
        value_ = nullptr;
        return detached;
    }
    void attach(T* value) noexcept {
        reset();
        value_ = value;
    }
    void reset() noexcept {
        if (value_ != nullptr) {
            value_->Release();
            value_ = nullptr;
        }
    }
    T* operator->() const noexcept { return value_; }
    explicit operator bool() const noexcept { return value_ != nullptr; }

private:
    T* value_{};
};

struct mf_lifetime {
    HRESULT co_result{CoInitializeEx(nullptr, COINIT_MULTITHREADED)};
    bool co_initialized{SUCCEEDED(co_result)};
    HRESULT mf_result{FAILED(co_result) ? co_result : MFStartup(MF_VERSION, MFSTARTUP_FULL)};
    bool mf_initialized{SUCCEEDED(mf_result)};
    ~mf_lifetime() {
        if (mf_initialized) static_cast<void>(MFShutdown());
        if (co_initialized) CoUninitialize();
    }
};

struct encoded_sample_facts {
    int64_t time_100ns{};
    int64_t duration_100ns{};
    DWORD byte_count{};
    bool clean_point_present{};
    UINT32 clean_point{};
    bool decode_timestamp_present{};
    UINT64 decode_timestamp{};
    std::vector<std::vector<uint8_t>> buffer_bytes;
};

struct retained_encoded_sample {
    encoded_sample_facts facts;
    com_ptr<IMFSample> sample;
};

struct pcm_sample_facts {
    LONGLONG time_100ns{};
    LONGLONG duration_100ns{};
    DWORD buffer_count{};
    DWORD byte_count{};
};

struct retained_pcm_sample {
    pcm_sample_facts facts;
    com_ptr<IMFSample> sample;
};

void print_hr(const char* stage, HRESULT hr) {
    std::cerr << "FAIL stage=" << stage << " HRESULT=0x"
        << std::uppercase << std::hex << std::setw(8) << std::setfill('0')
        << static_cast<uint32_t>(hr) << std::dec << std::setfill(' ') << '\n';
}

bool read_bytes(const std::filesystem::path& path, std::vector<uint8_t>& bytes) {
    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input.is_open()) return false;
    const std::streamoff end = input.tellg();
    if (end <= 0 || static_cast<uint64_t>(end) >
            static_cast<uint64_t>((std::numeric_limits<size_t>::max)()) ||
        static_cast<uint64_t>(end) > static_cast<uint64_t>((std::numeric_limits<std::streamsize>::max)())) {
        return false;
    }
    bytes.resize(static_cast<size_t>(end));
    input.seekg(0, std::ios::beg);
    input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return input.gcount() == static_cast<std::streamsize>(bytes.size());
}

bool source_profile_and_timeline(
    const std::filesystem::path& path,
    lpb::media::isobmff_video_profile& profile,
    isobmff_video_presentation_timeline& timeline) {
    std::vector<uint8_t> bytes;
    if (!read_bytes(path, bytes)) {
        std::cerr << "FAIL stage=read-immutable-apple-source\n";
        return false;
    }
    lpb::memory_random_access memory{bytes};
    lpb::media::video_profile_failure profile_failure{};
    if (!lpb::media::inspect_isobmff_video_profile(memory.view(), profile, profile_failure) ||
        !inspect_isobmff_video_presentation_timeline(bytes, timeline)) {
        std::cerr << "FAIL stage=strict-apple-profile-or-timeline profile_failure="
            << static_cast<unsigned>(profile_failure) << '\n';
        return false;
    }
    const auto is_codec = [](uint32_t value, const char a, const char b, const char c, const char d) {
        const uint32_t expected = (static_cast<uint32_t>(static_cast<uint8_t>(a)) << 24U) |
            (static_cast<uint32_t>(static_cast<uint8_t>(b)) << 16U) |
            (static_cast<uint32_t>(static_cast<uint8_t>(c)) << 8U) |
            static_cast<uint32_t>(static_cast<uint8_t>(d));
        return value == expected;
    };
    if ((!is_codec(profile.codec_fourcc, 'h', 'v', 'c', '1') &&
            !is_codec(profile.codec_fourcc, 'h', 'e', 'v', '1')) ||
        profile.classification != lpb::media::video_profile_class::ordinary_sdr ||
        profile.bit_depth_luma != 8U || profile.bit_depth_chroma != 8U ||
        profile.color_primaries != 12U || profile.transfer_characteristics != 1U ||
        profile.matrix_coefficients != 6U || profile.full_range_known == 0U ||
        profile.full_range == 0U || timeline.sample_count != 61U ||
        timeline.visible_sample_count != 55U ||
        timeline.presentation_order.size() != timeline.sample_count) {
        std::cerr << "FAIL stage=frozen-apple-real-sample-precondition coded=" << timeline.sample_count
            << " visible=" << timeline.visible_sample_count << '\n';
        return false;
    }
    std::cout << "SOURCE Apple MOV strict_profile=HEVC/8-bit/Display-P3/BT709/full-range"
        << " coded=" << timeline.sample_count << " visible=" << timeline.visible_sample_count
        << " timescale=" << timeline.media_timescale << '\n';
    return true;
}

bool get_friendly_name(IMFActivate* activation, std::wstring& name) {
    LPWSTR allocated = nullptr;
    UINT32 length = 0;
    const HRESULT hr = activation->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &allocated, &length);
    if (FAILED(hr) || allocated == nullptr) return false;
    name.assign(allocated, length);
    CoTaskMemFree(allocated);
    return true;
}

bool create_encoder_output_type(
    UINT32 width,
    UINT32 height,
    UINT32 fps_num,
    UINT32 fps_den,
    UINT32 bitrate,
    bool include_nominal_rate,
    com_ptr<IMFMediaType>& output_type) {
    HRESULT hr = MFCreateMediaType(output_type.put());
    if (SUCCEEDED(hr)) hr = output_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = output_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    if (SUCCEEDED(hr)) hr = output_type->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(output_type.get(), MF_MT_FRAME_SIZE, width, height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(output_type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1U, 1U);
    if (SUCCEEDED(hr)) hr = output_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = output_type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_Display_P3);
    if (SUCCEEDED(hr)) hr = output_type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709);
    if (SUCCEEDED(hr)) hr = output_type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT601);
    if (SUCCEEDED(hr)) hr = output_type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_0_255);
    if (SUCCEEDED(hr) && include_nominal_rate) {
        hr = MFSetAttributeRatio(output_type.get(), MF_MT_FRAME_RATE, fps_num, fps_den);
    }
    return SUCCEEDED(hr);
}

bool copy_input_type(IMFMediaType* source, bool include_nominal_rate,
    UINT32 fps_num, UINT32 fps_den, com_ptr<IMFMediaType>& output) {
    HRESULT hr = MFCreateMediaType(output.put());
    if (SUCCEEDED(hr)) hr = source->CopyAllItems(output.get());
    if (SUCCEEDED(hr) && !include_nominal_rate) {
        const HRESULT delete_hr = output->DeleteItem(MF_MT_FRAME_RATE);
        if (FAILED(delete_hr) && delete_hr != MF_E_ATTRIBUTENOTFOUND) hr = delete_hr;
    } else if (SUCCEEDED(hr) && (fps_num == 0U || fps_den == 0U)) {
        hr = MF_E_INVALIDMEDIATYPE;
    } else if (SUCCEEDED(hr)) {
        hr = MFSetAttributeRatio(output.get(), MF_MT_FRAME_RATE, fps_num, fps_den);
    }
    return SUCCEEDED(hr);
}

bool media_type_is_nv12(IMFMediaType* type, UINT32 expected_width, UINT32 expected_height) {
    if (type == nullptr) return false;
    GUID major = GUID_NULL;
    GUID subtype = GUID_NULL;
    UINT32 width = 0;
    UINT32 height = 0;
    return SUCCEEDED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) && major == MFMediaType_Video &&
        SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) && subtype == MFVideoFormat_NV12 &&
        SUCCEEDED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height)) &&
        width == expected_width && height == expected_height;
}

bool get_encoder_nv12_type(IMFTransform* transform, DWORD input_stream,
    UINT32 expected_width, UINT32 expected_height, com_ptr<IMFMediaType>& matching_type) {
    for (DWORD index = 0; index < 64U; ++index) {
        com_ptr<IMFMediaType> available;
        const HRESULT hr = transform->GetInputAvailableType(input_stream, index, available.put());
        if (hr == MF_E_NO_MORE_TYPES) return false;
        if (FAILED(hr)) return false;
        if (media_type_is_nv12(available.get(), expected_width, expected_height)) {
            matching_type = std::move(available);
            return true;
        }
    }
    return false;
}

bool set_encoder_input_type(
    IMFTransform* transform,
    DWORD input_stream,
    IMFMediaType* base_type,
    UINT32 fps_num,
    UINT32 fps_den,
    bool& uses_nominal_rate) {
    com_ptr<IMFMediaType> type_without_rate;
    if (!copy_input_type(base_type, false, fps_num, fps_den, type_without_rate)) return false;
    HRESULT hr = transform->SetInputType(input_stream, type_without_rate.get(), MFT_SET_TYPE_TEST_ONLY);
    uses_nominal_rate = false;
    if (SUCCEEDED(hr)) {
        hr = transform->SetInputType(input_stream, type_without_rate.get(), 0U);
        if (SUCCEEDED(hr)) return true;
    }

    com_ptr<IMFMediaType> type_with_rate;
    if (!copy_input_type(base_type, true, fps_num, fps_den, type_with_rate)) {
        print_hr("create-NV12-input-type-with-source-nominal-rate", hr);
        return false;
    }
    hr = transform->SetInputType(input_stream, type_with_rate.get(), MFT_SET_TYPE_TEST_ONLY);
    if (SUCCEEDED(hr)) hr = transform->SetInputType(input_stream, type_with_rate.get(), 0U);
    if (FAILED(hr)) {
        print_hr("set-NV12-input-type-from-negotiated-base", hr);
        return false;
    }
    uses_nominal_rate = true;
    return true;
}

bool configure_encoder(
    IMFTransform* transform,
    UINT32 width,
    UINT32 height,
    UINT32 fps_num,
    UINT32 fps_den,
    UINT32 bitrate,
    DWORD& input_stream,
    DWORD& output_stream,
    bool& output_uses_nominal_rate,
    bool& input_uses_nominal_rate,
    com_ptr<IMFMediaType>& configured_h264_type) {
    DWORD input_count = 0;
    DWORD output_count = 0;
    HRESULT hr = transform->GetStreamIDs(1U, &input_stream, 1U, &output_stream);
    if (hr == E_NOTIMPL) {
        input_stream = 0U;
        output_stream = 0U;
    } else if (FAILED(hr)) {
        print_hr("GetStreamIDs", hr);
        return false;
    }
    hr = transform->GetStreamCount(&input_count, &output_count);
    if (FAILED(hr) || input_count != 1U || output_count != 1U) {
        print_hr("validate-single-encoder-stream", FAILED(hr) ? hr : MF_E_INVALIDSTREAMNUMBER);
        return false;
    }

    com_ptr<IMFMediaType> output_without_rate;
    if (!create_encoder_output_type(width, height, fps_num, fps_den, bitrate, false, output_without_rate)) {
        std::cerr << "FAIL stage=create-h264-output-media-type\n";
        return false;
    }
    hr = transform->SetOutputType(output_stream, output_without_rate.get(), MFT_SET_TYPE_TEST_ONLY);
    output_uses_nominal_rate = false;
    if (FAILED(hr)) {
        com_ptr<IMFMediaType> output_with_rate;
        if (fps_num == 0U || fps_den == 0U ||
            !create_encoder_output_type(width, height, fps_num, fps_den, bitrate, true, output_with_rate)) {
            print_hr("H264-output-type-without-rate-and-no-source-rate", hr);
            return false;
        }
        hr = transform->SetOutputType(output_stream, output_with_rate.get(), MFT_SET_TYPE_TEST_ONLY);
        if (FAILED(hr)) {
            print_hr("test-H264-output-type-with-source-nominal-rate", hr);
            return false;
        }
        output_uses_nominal_rate = true;
        hr = transform->SetOutputType(output_stream, output_with_rate.get(), 0U);
        if (FAILED(hr)) {
            print_hr("set-H264-output-type-with-source-nominal-rate", hr);
            return false;
        }
        configured_h264_type = std::move(output_with_rate);
    } else {
        hr = transform->SetOutputType(output_stream, output_without_rate.get(), 0U);
        if (FAILED(hr)) {
            com_ptr<IMFMediaType> output_with_rate;
            if (fps_num == 0U || fps_den == 0U ||
                !create_encoder_output_type(width, height, fps_num, fps_den, bitrate, true, output_with_rate)) {
                print_hr("set-H264-output-type-without-rate-and-no-source-rate", hr);
                return false;
            }
            hr = transform->SetOutputType(output_stream, output_with_rate.get(), MFT_SET_TYPE_TEST_ONLY);
            if (SUCCEEDED(hr)) hr = transform->SetOutputType(output_stream, output_with_rate.get(), 0U);
            if (FAILED(hr)) {
                print_hr("set-H264-output-type-with-source-nominal-rate", hr);
                return false;
            }
            output_uses_nominal_rate = true;
            configured_h264_type = std::move(output_with_rate);
        } else {
            configured_h264_type = std::move(output_without_rate);
        }
    }

    com_ptr<IMFMediaType> encoder_nv12_type;
    if (!get_encoder_nv12_type(transform, input_stream, width, height, encoder_nv12_type)) {
        std::cerr << "FAIL stage=encoder-does-not-advertise-matching-NV12-input\n";
        return false;
    }
    if (!set_encoder_input_type(transform, input_stream, encoder_nv12_type.get(),
            fps_num, fps_den, input_uses_nominal_rate)) {
        std::cerr << "FAIL stage=negotiate-MFT-advertised-NV12-input-type\n";
        return false;
    }

    com_ptr<IMFMediaType> actual_h264_type;
    hr = transform->GetOutputCurrentType(output_stream, actual_h264_type.put());
    if (FAILED(hr)) {
        print_hr("GetOutputCurrentType", hr);
        return false;
    }
    GUID major = GUID_NULL;
    GUID subtype = GUID_NULL;
    if (FAILED(actual_h264_type->GetGUID(MF_MT_MAJOR_TYPE, &major)) ||
        FAILED(actual_h264_type->GetGUID(MF_MT_SUBTYPE, &subtype)) ||
        major != MFMediaType_Video || subtype != MFVideoFormat_H264) {
        std::cerr << "FAIL stage=validate-negotiated-H264-output-type\n";
        return false;
    }
    configured_h264_type = std::move(actual_h264_type);
    return true;
}

bool snapshot_sample_facts(IMFSample* sample, encoded_sample_facts& facts) {
    if (sample == nullptr) {
        std::cerr << "FAIL stage=null-compressed-sample\n";
        return false;
    }
    LONGLONG time = 0;
    LONGLONG duration = 0;
    HRESULT hr = sample->GetSampleTime(&time);
    if (FAILED(hr)) {
        print_hr("compressed-sample-timestamp-missing", hr);
        return false;
    }
    hr = sample->GetSampleDuration(&duration);
    if (FAILED(hr) || time < 0 || duration <= 0) {
        print_hr("compressed-sample-duration-invalid", FAILED(hr) ? hr : MF_E_NO_SAMPLE_DURATION);
        return false;
    }

    facts.time_100ns = time;
    facts.duration_100ns = duration;
    hr = sample->GetUINT32(MFSampleExtension_CleanPoint, &facts.clean_point);
    if (hr == MF_E_ATTRIBUTENOTFOUND) {
        facts.clean_point_present = false;
    } else if (FAILED(hr)) {
        print_hr("compressed-sample-clean-point", hr);
        return false;
    } else {
        facts.clean_point_present = true;
    }

    hr = sample->GetUINT64(MFSampleExtension_DecodeTimestamp, &facts.decode_timestamp);
    if (hr == MF_E_ATTRIBUTENOTFOUND) {
        facts.decode_timestamp_present = false;
    } else if (FAILED(hr)) {
        print_hr("compressed-sample-decode-timestamp", hr);
        return false;
    } else {
        facts.decode_timestamp_present = true;
    }

    DWORD buffer_count = 0U;
    hr = sample->GetBufferCount(&buffer_count);
    if (FAILED(hr) || buffer_count == 0U) {
        print_hr("compressed-sample-buffer-count", FAILED(hr) ? hr : E_UNEXPECTED);
        return false;
    }
    facts.buffer_bytes.clear();
    facts.buffer_bytes.reserve(buffer_count);
    uint64_t total_bytes = 0U;
    for (DWORD index = 0U; index < buffer_count; ++index) {
        com_ptr<IMFMediaBuffer> buffer;
        hr = sample->GetBufferByIndex(index, buffer.put());
        if (FAILED(hr)) {
            print_hr("compressed-sample-buffer-access", hr);
            return false;
        }
        BYTE* data = nullptr;
        DWORD maximum_length = 0U;
        DWORD current_length = 0U;
        hr = buffer->Lock(&data, &maximum_length, &current_length);
        if (FAILED(hr)) {
            print_hr("compressed-sample-buffer-lock", hr);
            return false;
        }
        const bool valid_buffer = current_length <= maximum_length &&
            (current_length == 0U || data != nullptr);
        if (valid_buffer && current_length != 0U) {
            facts.buffer_bytes.emplace_back(data, data + current_length);
        } else if (valid_buffer) {
            facts.buffer_bytes.emplace_back();
        }
        const HRESULT unlock_hr = buffer->Unlock();
        if (!valid_buffer || FAILED(unlock_hr)) {
            print_hr("compressed-sample-buffer-unlock-or-length",
                !valid_buffer ? E_UNEXPECTED : unlock_hr);
            return false;
        }
        total_bytes += current_length;
        if (total_bytes > (std::numeric_limits<DWORD>::max)()) {
            std::cerr << "FAIL stage=compressed-sample-byte-count-overflow\n";
            return false;
        }
    }
    if (total_bytes == 0U) {
        std::cerr << "FAIL stage=encoder-output-empty-H264-bytes\n";
        return false;
    }
    facts.byte_count = static_cast<DWORD>(total_bytes);
    return true;
}

bool same_sample_facts(const encoded_sample_facts& left, const encoded_sample_facts& right) {
    return left.time_100ns == right.time_100ns &&
        left.duration_100ns == right.duration_100ns &&
        left.byte_count == right.byte_count &&
        left.clean_point_present == right.clean_point_present &&
        left.clean_point == right.clean_point &&
        left.decode_timestamp_present == right.decode_timestamp_present &&
        left.decode_timestamp == right.decode_timestamp &&
        left.buffer_bytes == right.buffer_bytes;
}

bool append_output_sample(IMFSample* sample, std::vector<retained_encoded_sample>& output) {
    retained_encoded_sample retained{};
    if (!snapshot_sample_facts(sample, retained.facts)) return false;
    sample->AddRef();
    retained.sample.attach(sample);
    output.push_back(std::move(retained));
    return true;
}

enum class drain_result {
    need_input,
    failed
};

drain_result drain_encoder_output(
    IMFTransform* transform,
    DWORD output_stream,
    const MFT_OUTPUT_STREAM_INFO& output_info,
    std::vector<retained_encoded_sample>& output) {
    for (size_t iteration = 0; iteration < 256U; ++iteration) {
        com_ptr<IMFSample> caller_sample;
        MFT_OUTPUT_DATA_BUFFER data{};
        data.dwStreamID = output_stream;
        const bool transform_allocates =
            (output_info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0U ||
            (output_info.dwFlags & MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES) != 0U;
        if (!transform_allocates) {
            if (output_info.cbSize == 0U) {
                std::cerr << "FAIL stage=encoder-requires-empty-caller-output-buffer\n";
                return drain_result::failed;
            }
            com_ptr<IMFMediaBuffer> buffer;
            HRESULT hr = MFCreateSample(caller_sample.put());
            if (SUCCEEDED(hr)) hr = MFCreateMemoryBuffer(output_info.cbSize, buffer.put());
            if (SUCCEEDED(hr)) hr = caller_sample->AddBuffer(buffer.get());
            if (FAILED(hr)) {
                print_hr("allocate-caller-output-sample", hr);
                return drain_result::failed;
            }
            data.pSample = caller_sample.get();
        }

        DWORD process_status = 0U;
        const HRESULT hr = transform->ProcessOutput(0U, 1U, &data, &process_status);
        IMFSample* produced_sample = data.pSample;
        if (data.pEvents != nullptr) data.pEvents->Release();

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
            if (produced_sample != nullptr && produced_sample != caller_sample.get()) produced_sample->Release();
            return drain_result::need_input;
        }
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
            if (produced_sample != nullptr && produced_sample != caller_sample.get()) produced_sample->Release();
            print_hr("unsupported-encoder-output-stream-change", hr);
            return drain_result::failed;
        }
        if (FAILED(hr)) {
            if (produced_sample != nullptr && produced_sample != caller_sample.get()) produced_sample->Release();
            print_hr("ProcessOutput", hr);
            return drain_result::failed;
        }
        if ((process_status & MFT_PROCESS_OUTPUT_STATUS_NEW_STREAMS) != 0U || produced_sample == nullptr) {
            if (produced_sample != nullptr && produced_sample != caller_sample.get()) produced_sample->Release();
            std::cerr << "FAIL stage=encoder-output-stream-changed-or-no-sample status=" << process_status << '\n';
            return drain_result::failed;
        }
        const bool appended = append_output_sample(produced_sample, output);
        if (produced_sample != caller_sample.get()) produced_sample->Release();
        if (!appended) return drain_result::failed;
    }
    std::cerr << "FAIL stage=encoder-output-drain-limit\n";
    return drain_result::failed;
}

bool run_encoder(
    IMFTransform* transform,
    DWORD input_stream,
    DWORD output_stream,
    IMFSourceReader* reader,
    UINT32 width,
    UINT32 height,
    const isobmff_video_presentation_timeline& source_timeline,
    const MFT_OUTPUT_STREAM_INFO& output_info,
    std::vector<retained_encoded_sample>& encoded) {
    HRESULT hr = transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0U);
    if (SUCCEEDED(hr)) hr = transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, input_stream);
    if (FAILED(hr)) {
        print_hr("MFT-begin-streaming", hr);
        return false;
    }

    size_t timeline_index = 0U;
    uint32_t decoded_samples = 0U;
    uint32_t visible_inputs = 0U;
    bool initial_reader_type_change_confirmed = false;
    bool end_of_stream = false;
    while (!end_of_stream) {
        DWORD actual_stream = 0U;
        DWORD flags = 0U;
        LONGLONG reader_time = 0;
        com_ptr<IMFSample> sample;
        hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0U,
            &actual_stream, &flags, &reader_time, sample.put());
        if (FAILED(hr)) {
            print_hr("SourceReader-ReadSample", hr);
            return false;
        }
        if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0U) {
            if (decoded_samples != 0U || initial_reader_type_change_confirmed) {
                std::cerr << "FAIL stage=SourceReader-media-type-changed-after-first-decoded-sample\n";
                return false;
            }
            com_ptr<IMFMediaType> changed_type;
            hr = reader->GetCurrentMediaType(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), changed_type.put());
            if (FAILED(hr) || !media_type_is_nv12(changed_type.get(), width, height)) {
                print_hr("validate-initial-SourceReader-type-change", FAILED(hr) ? hr : MF_E_INVALIDMEDIATYPE);
                return false;
            }
            initial_reader_type_change_confirmed = true;
            std::cout << "SOURCE_READER initial_type_change=verified_NV12_1920x1440\n";
        }
        if (sample) {
            if (timeline_index >= source_timeline.presentation_order.size()) {
                std::cerr << "FAIL stage=decoded-sample-count-exceeds-Native-timeline\n";
                return false;
            }
            const auto& entry = source_timeline.presentation_order[timeline_index++];
            ++decoded_samples;
            if (entry.visible) {
                hr = sample->SetSampleTime(entry.normalized_time_100ns);
                if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(entry.duration_100ns);
                if (FAILED(hr)) {
                    print_hr("set-Native-authoritative-visible-sample-time", hr);
                    return false;
                }

                bool accepted = false;
                for (size_t attempt = 0; attempt < 4U; ++attempt) {
                    hr = transform->ProcessInput(input_stream, sample.get(), 0U);
                    if (hr == MF_E_NOTACCEPTING) {
                        const size_t output_before = encoded.size();
                        if (drain_encoder_output(transform, output_stream, output_info, encoded) == drain_result::failed ||
                            encoded.size() == output_before) {
                            std::cerr << "FAIL stage=encoder-refused-input-without-progress\n";
                            return false;
                        }
                        continue;
                    }
                    if (FAILED(hr)) {
                        print_hr("ProcessInput", hr);
                        return false;
                    }
                    accepted = true;
                    ++visible_inputs;
                    break;
                }
                if (!accepted) {
                    std::cerr << "FAIL stage=encoder-input-retry-limit\n";
                    return false;
                }
                if (drain_encoder_output(transform, output_stream, output_info, encoded) == drain_result::failed) {
                    return false;
                }
            }
        }
        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0U) end_of_stream = true;
        if ((flags & MF_SOURCE_READERF_STREAMTICK) != 0U && !sample) {
            std::cerr << "FAIL stage=unexpected-video-stream-tick-without-decoded-sample\n";
            return false;
        }
    }

    if (decoded_samples != source_timeline.sample_count ||
        timeline_index != source_timeline.presentation_order.size() ||
        visible_inputs != source_timeline.visible_sample_count) {
        std::cerr << "FAIL stage=source-reader-native-timeline-cardinality decoded=" << decoded_samples
            << " timeline=" << source_timeline.sample_count << " fed_visible=" << visible_inputs
            << " expected_visible=" << source_timeline.visible_sample_count << '\n';
        return false;
    }
    hr = transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, input_stream);
    if (SUCCEEDED(hr)) hr = transform->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0U);
    if (FAILED(hr)) {
        print_hr("MFT-drain-command", hr);
        return false;
    }
    if (drain_encoder_output(transform, output_stream, output_info, encoded) == drain_result::failed) return false;
    hr = transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0U);
    if (FAILED(hr)) {
        print_hr("MFT-end-streaming", hr);
        return false;
    }
    std::cout << "MFT_INPUTS decoded=" << decoded_samples << " native_visible_submitted=" << visible_inputs
        << " coded_hidden_preroll_skipped=" << (decoded_samples - visible_inputs) << '\n';
    return true;
}

bool verify_encoded_timing(
    const isobmff_video_presentation_timeline& source,
    const std::vector<retained_encoded_sample>& encoded) {
    struct timing_fact {
        int64_t time_100ns{};
        int64_t duration_100ns{};
        DWORD byte_count{};
    };
    std::vector<timing_fact> ordered_timing;
    ordered_timing.reserve(encoded.size());
    for (const auto& sample : encoded) {
        ordered_timing.push_back(timing_fact{
            sample.facts.time_100ns, sample.facts.duration_100ns, sample.facts.byte_count});
    }
    std::sort(ordered_timing.begin(), ordered_timing.end(), [](const auto& left, const auto& right) {
        return left.time_100ns < right.time_100ns;
    });
    if (encoded.size() != source.visible_sample_count ||
        encoded.size() > static_cast<size_t>((std::numeric_limits<uint32_t>::max)())) {
        std::cerr << "FAIL stage=direct-MFT-visible-frame-count input=" << source.visible_sample_count
            << " output=" << encoded.size() << '\n';
        return false;
    }
    isobmff_video_presentation_timeline output{};
    output.movie_timescale = 10000000U;
    output.media_timescale = 10000000U;
    output.sample_count = static_cast<uint32_t>(encoded.size());
    output.visible_sample_count = output.sample_count;
    output.presentation_order.reserve(ordered_timing.size());
    for (size_t index = 0; index < ordered_timing.size(); ++index) {
        const auto& sample = ordered_timing[index];
        if (sample.time_100ns < 0 || sample.duration_100ns <= 0 || sample.byte_count == 0U ||
            (index != 0U && ordered_timing[index - 1U].time_100ns >= sample.time_100ns)) {
            std::cerr << "FAIL stage=direct-MFT-output-sample-invalid-or-duplicate-PTS frame=" << index << '\n';
            return false;
        }
        output.presentation_order.push_back(isobmff_video_presentation_sample{
            static_cast<uint32_t>(index), sample.time_100ns,
            static_cast<uint64_t>(sample.duration_100ns), true,
            sample.time_100ns, sample.duration_100ns});
    }
    uint32_t failing_frame = 0U;
    if (!lpb::media::presentation_timing_within_source_frame_intervals(source, output, failing_frame)) {
        std::cerr << "FAIL stage=direct-MFT-source-frame-interval-rule frame=" << failing_frame << '\n';
        return false;
    }
    uint64_t total_bytes = 0U;
    for (const auto& sample : ordered_timing) total_bytes += sample.byte_count;
    std::cout << "DIRECT_MFT_ENCODE_GATE input_visible=" << source.visible_sample_count
        << " output_h264_samples=" << encoded.size() << " unique_pts=PASS durations=PASS bytes=PASS"
        << " frozen_source_frame_interval_rule=PASS emission_order_retained=1 total_bytes=" << total_bytes << '\n';
    return true;
}

bool get_final_h264_type(
    IMFTransform* encoder,
    DWORD output_stream,
    UINT32 expected_width,
    UINT32 expected_height,
    bool nominal_rate_was_configured,
    com_ptr<IMFMediaType>& final_type) {
    HRESULT hr = encoder->GetOutputCurrentType(output_stream, final_type.put());
    if (FAILED(hr)) {
        print_hr("GetOutputCurrentType-after-drain", hr);
        return false;
    }
    GUID major = GUID_NULL;
    GUID subtype = GUID_NULL;
    UINT32 width = 0U;
    UINT32 height = 0U;
    if (FAILED(final_type->GetGUID(MF_MT_MAJOR_TYPE, &major)) ||
        FAILED(final_type->GetGUID(MF_MT_SUBTYPE, &subtype)) ||
        FAILED(MFGetAttributeSize(final_type.get(), MF_MT_FRAME_SIZE, &width, &height)) ||
        major != MFMediaType_Video || subtype != MFVideoFormat_H264 ||
        width != expected_width || height != expected_height) {
        std::cerr << "FAIL stage=validate-final-H264-type-after-drain\n";
        return false;
    }
    UINT32 fps_num = 0U;
    UINT32 fps_den = 0U;
    if (nominal_rate_was_configured) {
        hr = MFGetAttributeRatio(final_type.get(), MF_MT_FRAME_RATE, &fps_num, &fps_den);
        if (FAILED(hr) || fps_num == 0U || fps_den == 0U) {
            print_hr("validate-final-H264-configured-nominal-rate", FAILED(hr) ? hr : MF_E_INVALIDMEDIATYPE);
            return false;
        }
    }
    std::cout << "MFT_FINAL_TYPE video=H264 dimensions=" << width << 'x' << height
        << " configured_nominal_rate=" << (nominal_rate_was_configured ? "valid" : "absent-or-unused");
    if (nominal_rate_was_configured) std::cout << " rate=" << fps_num << '/' << fps_den;
    std::cout << '\n';
    return true;
}

bool get_transform_friendly_name(IMFTransform* transform, std::wstring& name) {
    com_ptr<IMFAttributes> attributes;
    HRESULT hr = transform->GetAttributes(attributes.put());
    if (FAILED(hr)) return false;
    LPWSTR allocated = nullptr;
    UINT32 length = 0U;
    hr = attributes->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &allocated, &length);
    if (FAILED(hr) || allocated == nullptr) return false;
    name.assign(allocated, length);
    CoTaskMemFree(allocated);
    return true;
}

bool pcm_type_is_44100_mono_16bit(IMFMediaType* type) {
    if (type == nullptr) return false;
    GUID major = GUID_NULL;
    GUID subtype = GUID_NULL;
    UINT32 bits = 0U;
    UINT32 sample_rate = 0U;
    UINT32 channels = 0U;
    UINT32 block_alignment = 0U;
    UINT32 average_bytes_per_second = 0U;
    return SUCCEEDED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) &&
        SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bits)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &sample_rate)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, &block_alignment)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, &average_bytes_per_second)) &&
        major == MFMediaType_Audio && subtype == MFAudioFormat_PCM && bits == 16U &&
        sample_rate == 44100U && channels == 1U && block_alignment == 2U &&
        average_bytes_per_second == 88200U;
}

bool snapshot_pcm_sample_facts(IMFSample* sample, pcm_sample_facts& facts) {
    if (sample == nullptr) {
        std::cerr << "FAIL stage=null-source-PCM-sample\n";
        return false;
    }
    LONGLONG time = 0;
    LONGLONG duration = 0;
    HRESULT hr = sample->GetSampleTime(&time);
    if (FAILED(hr) || time < 0) {
        print_hr("source-PCM-timestamp-missing-or-negative", FAILED(hr) ? hr : E_INVALIDARG);
        return false;
    }
    hr = sample->GetSampleDuration(&duration);
    if (FAILED(hr) || duration <= 0) {
        print_hr("source-PCM-duration-missing-or-nonpositive", FAILED(hr) ? hr : MF_E_NO_SAMPLE_DURATION);
        return false;
    }

    DWORD buffer_count = 0U;
    hr = sample->GetBufferCount(&buffer_count);
    if (FAILED(hr) || buffer_count == 0U) {
        print_hr("source-PCM-buffer-count", FAILED(hr) ? hr : E_UNEXPECTED);
        return false;
    }
    uint64_t total_bytes = 0U;
    for (DWORD index = 0U; index < buffer_count; ++index) {
        com_ptr<IMFMediaBuffer> buffer;
        hr = sample->GetBufferByIndex(index, buffer.put());
        if (FAILED(hr)) {
            print_hr("source-PCM-buffer-access", hr);
            return false;
        }
        DWORD current_length = 0U;
        hr = buffer->GetCurrentLength(&current_length);
        if (FAILED(hr)) {
            print_hr("source-PCM-buffer-length", hr);
            return false;
        }
        total_bytes += current_length;
        if (total_bytes > (std::numeric_limits<DWORD>::max)()) {
            std::cerr << "FAIL stage=source-PCM-byte-count-overflow\n";
            return false;
        }
    }
    if (total_bytes == 0U) {
        std::cerr << "FAIL stage=source-PCM-sample-has-no-audio-bytes\n";
        return false;
    }
    facts.time_100ns = time;
    facts.duration_100ns = duration;
    facts.buffer_count = buffer_count;
    facts.byte_count = static_cast<DWORD>(total_bytes);
    return true;
}

bool same_pcm_sample_facts(const pcm_sample_facts& left, const pcm_sample_facts& right) {
    return left.time_100ns == right.time_100ns &&
        left.duration_100ns == right.duration_100ns &&
        left.buffer_count == right.buffer_count && left.byte_count == right.byte_count;
}

bool read_source_pcm_audio(
    const std::filesystem::path& source_path,
    std::vector<retained_pcm_sample>& output,
    com_ptr<IMFMediaType>& verified_pcm_type) {
    com_ptr<IMFAttributes> reader_attributes;
    HRESULT hr = MFCreateAttributes(reader_attributes.put(), 1U);
    if (SUCCEEDED(hr)) {
        hr = reader_attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    }
    if (FAILED(hr)) {
        print_hr("configure-software-only-Apple-audio-SourceReader", hr);
        return false;
    }
    com_ptr<IMFSourceReader> reader;
    hr = MFCreateSourceReaderFromURL(source_path.c_str(), reader_attributes.get(), reader.put());
    if (FAILED(hr)) {
        print_hr("create-separate-Apple-audio-SourceReader", hr);
        return false;
    }
    hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    if (SUCCEEDED(hr)) {
        hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), TRUE);
    }
    if (FAILED(hr)) {
        print_hr("isolate-first-Apple-audio-stream", hr);
        return false;
    }

    com_ptr<IMFMediaType> native_audio_type;
    hr = reader->GetNativeMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0U, native_audio_type.put());
    if (FAILED(hr) || native_audio_type.get() == nullptr) {
        print_hr("get-native-Apple-audio-type", FAILED(hr) ? hr : E_UNEXPECTED);
        return false;
    }

    com_ptr<IMFMediaType> requested_pcm_type;
    hr = MFCreateMediaType(requested_pcm_type.put());
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16U);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100U);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 1U);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 2U);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 88200U);
    if (SUCCEEDED(hr)) {
        hr = reader->SetCurrentMediaType(
            static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), nullptr, requested_pcm_type.get());
    }
    if (FAILED(hr)) {
        print_hr("request-Apple-audio-PCM-s16le-44100-mono", hr);
        return false;
    }
    hr = reader->GetCurrentMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), verified_pcm_type.put());
    if (FAILED(hr) || !pcm_type_is_44100_mono_16bit(verified_pcm_type.get())) {
        print_hr("verify-actual-Apple-audio-PCM-s16le-44100-mono", FAILED(hr) ? hr : MF_E_INVALIDMEDIATYPE);
        return false;
    }

    LONGLONG previous_time = 0;
    bool has_previous_time = false;
    bool reached_end = false;
    while (!reached_end) {
        DWORD stream_index = 0U;
        DWORD flags = 0U;
        LONGLONG timestamp = 0;
        com_ptr<IMFSample> sample;
        hr = reader->ReadSample(
            static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0U,
            &stream_index, &flags, &timestamp, sample.put());
        if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR) != 0U ||
            (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0U) {
            print_hr("read-Apple-audio-PCM-sample", FAILED(hr) ? hr : MF_E_INVALIDMEDIATYPE);
            return false;
        }
        if (sample) {
            retained_pcm_sample retained{};
            if (!snapshot_pcm_sample_facts(sample.get(), retained.facts) ||
                (has_previous_time && retained.facts.time_100ns < previous_time)) {
                std::cerr << "FAIL stage=Apple-audio-PCM-timestamp-order index=" << output.size() << '\n';
                return false;
            }
            previous_time = retained.facts.time_100ns;
            has_previous_time = true;
            retained.sample = std::move(sample);
            output.push_back(std::move(retained));
        } else if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) == 0U) {
            std::cerr << "FAIL stage=Apple-audio-PCM-read-returned-no-sample-before-EOS flags=0x"
                << std::hex << flags << std::dec << '\n';
            return false;
        }
        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0U) reached_end = true;
    }
    if (output.empty()) {
        std::cerr << "FAIL stage=Apple-audio-PCM-source-produced-no-samples\n";
        return false;
    }
    std::cout << "SOURCE_AUDIO codec=PCM bits=16 sample_rate=44100 channels=1 samples="
        << output.size() << " timestamp_order=nondecreasing timestamps_and_durations=retained-exactly\n";
    return true;
}

bool write_bytes_create_only(
    const std::filesystem::path& output_path,
    std::span<const uint8_t> bytes) {
    if (bytes.empty()) {
        std::cerr << "FAIL stage=create-only-byte-output-empty\n";
        return false;
    }
    std::error_code filesystem_error;
    if (!output_path.parent_path().empty()) {
        std::filesystem::create_directories(output_path.parent_path(), filesystem_error);
        if (filesystem_error) {
            std::cerr << "FAIL stage=create-only-byte-output-directory error=" << filesystem_error.message() << '\n';
            return false;
        }
    }
    HANDLE file = CreateFileW(
        output_path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
        CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        std::cerr << "FAIL stage=create-only-byte-output GetLastError=" << GetLastError() << '\n';
        return false;
    }
    bool write_succeeded = true;
    size_t offset = 0U;
    while (offset < bytes.size()) {
        const size_t remaining = bytes.size() - offset;
        const DWORD requested = static_cast<DWORD>((std::min)(
            remaining, static_cast<size_t>((std::numeric_limits<DWORD>::max)())));
        DWORD written = 0U;
        if (!WriteFile(file, bytes.data() + offset, requested, &written, nullptr) || written != requested) {
            std::cerr << "FAIL stage=create-only-byte-output-write GetLastError=" << GetLastError() << '\n';
            write_succeeded = false;
            break;
        }
        offset += written;
    }
    if (write_succeeded && !FlushFileBuffers(file)) {
        std::cerr << "FAIL stage=create-only-byte-output-flush GetLastError=" << GetLastError() << '\n';
        write_succeeded = false;
    }
    if (!CloseHandle(file)) {
        std::cerr << "FAIL stage=create-only-byte-output-close GetLastError=" << GetLastError() << '\n';
        write_succeeded = false;
    }
    return write_succeeded;
}

bool prove_audio_sink_transforms(IMFSinkWriterEx* writer, DWORD stream_index, size_t& transform_count) {
    transform_count = 0U;
    for (DWORD index = 0U;; ++index) {
        GUID category = GUID_NULL;
        com_ptr<IMFTransform> transform;
        const HRESULT hr = writer->GetTransformForStream(stream_index, index, &category, transform.put());
        if (hr == MF_E_INVALIDINDEX && !transform) break;
        if (FAILED(hr) || !transform) {
            print_hr("enumerate-SinkWriter-AAC-audio-transform-chain", FAILED(hr) ? hr : E_UNEXPECTED);
            return false;
        }
        std::wstring friendly_name;
        const bool has_name = get_transform_friendly_name(transform.get(), friendly_name);
        std::wcout << L"SINKWRITER_TRANSFORM stream=audio index=" << index << L" category={"
            << std::hex << category.Data1 << L'-' << category.Data2 << L'-' << category.Data3
            << L'-' << static_cast<unsigned>(category.Data4[0]) << static_cast<unsigned>(category.Data4[1])
            << L'-' << static_cast<unsigned>(category.Data4[2]) << static_cast<unsigned>(category.Data4[3])
            << static_cast<unsigned>(category.Data4[4]) << static_cast<unsigned>(category.Data4[5])
            << static_cast<unsigned>(category.Data4[6]) << static_cast<unsigned>(category.Data4[7])
            << std::dec << L"} friendly_name=" << (has_name ? friendly_name : L"<unavailable>") << L'\n';
        ++transform_count;
        if (index == (std::numeric_limits<DWORD>::max)()) {
            std::cerr << "FAIL stage=SinkWriter-audio-transform-enumeration-did-not-terminate\n";
            return false;
        }
    }
    if (transform_count == 0U) {
        std::cerr << "FAIL stage=SinkWriter-AAC-audio-transform-chain-empty\n";
        return false;
    }
    std::cout << "SINKWRITER_TRANSFORMS stream=audio enumerated=" << transform_count
        << " proof=GetTransformForStream(count)->MF_E_INVALIDINDEX\n";
    return true;
}

bool write_compressed_mux(
    const std::filesystem::path& output_path,
    IMFMediaType* final_h264_type,
    IMFMediaType* verified_pcm_type,
    const std::vector<retained_encoded_sample>& encoded,
    const std::vector<retained_pcm_sample>& audio_samples) {
    if (final_h264_type == nullptr || !pcm_type_is_44100_mono_16bit(verified_pcm_type) ||
        encoded.empty() || audio_samples.empty()) {
        std::cerr << "FAIL stage=compressed-A-V-mux-input-types-or-samples-invalid\n";
        return false;
    }
    std::error_code filesystem_error;
    if (!output_path.parent_path().empty()) {
        std::filesystem::create_directories(output_path.parent_path(), filesystem_error);
        if (filesystem_error) {
            std::cerr << "FAIL stage=create-mux-output-directory error=" << filesystem_error.message() << '\n';
            return false;
        }
    }
    HANDLE created_file = CreateFileW(
        output_path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (created_file == INVALID_HANDLE_VALUE) {
        std::cerr << "FAIL stage=create-only-mux-output GetLastError=" << GetLastError() << '\n';
        return false;
    }
    if (!CloseHandle(created_file)) {
        std::cerr << "FAIL stage=close-create-only-mux-output GetLastError=" << GetLastError() << '\n';
        return false;
    }

    com_ptr<IMFByteStream> byte_stream;
    HRESULT hr = MFCreateFile(
        MF_ACCESSMODE_READWRITE, MF_OPENMODE_FAIL_IF_NOT_EXIST, MF_FILEFLAGS_NONE,
        output_path.c_str(), byte_stream.put());
    if (FAILED(hr)) {
        print_hr("open-new-mux-output-byte-stream-without-truncation", hr);
        return false;
    }
    com_ptr<IMFAttributes> writer_attributes;
    hr = MFCreateAttributes(writer_attributes.put(), 2U);
    if (SUCCEEDED(hr)) {
        hr = writer_attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    }
    if (SUCCEEDED(hr)) {
        hr = writer_attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
    }
    if (FAILED(hr)) {
        print_hr("configure-software-only-MP4-SinkWriter", hr);
        return false;
    }
    com_ptr<IMFSinkWriter> writer;
    hr = MFCreateSinkWriterFromURL(nullptr, byte_stream.get(), writer_attributes.get(), writer.put());
    if (FAILED(hr)) {
        print_hr("create-MP4-SinkWriter-from-create-only-byte-stream", hr);
        return false;
    }

    DWORD stream_index = 0U;
    hr = writer->AddStream(final_h264_type, &stream_index);
    if (FAILED(hr)) {
        print_hr("SinkWriter-AddStream-exact-final-H264-type", hr);
        return false;
    }
    hr = writer->SetInputMediaType(stream_index, final_h264_type, nullptr);
    if (FAILED(hr)) {
        print_hr("SinkWriter-SetInputMediaType-exact-same-final-H264-type", hr);
        return false;
    }
    com_ptr<IMFMediaType> output_aac_type;
    hr = MFCreateMediaType(output_aac_type.put());
    if (SUCCEEDED(hr)) hr = output_aac_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    if (SUCCEEDED(hr)) hr = output_aac_type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
    if (SUCCEEDED(hr)) hr = output_aac_type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16U);
    if (SUCCEEDED(hr)) hr = output_aac_type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100U);
    if (SUCCEEDED(hr)) hr = output_aac_type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 1U);
    if (SUCCEEDED(hr)) hr = output_aac_type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 12000U);
    DWORD audio_stream_index = 0U;
    if (SUCCEEDED(hr)) hr = writer->AddStream(output_aac_type.get(), &audio_stream_index);
    if (FAILED(hr)) {
        print_hr("SinkWriter-AddStream-AAC-44100-mono", hr);
        return false;
    }
    hr = writer->SetInputMediaType(audio_stream_index, verified_pcm_type, nullptr);
    if (FAILED(hr)) {
        print_hr("SinkWriter-SetInputMediaType-verified-PCM-s16le-44100-mono", hr);
        return false;
    }
    hr = writer->BeginWriting();
    if (FAILED(hr)) {
        print_hr("SinkWriter-BeginWriting", hr);
        return false;
    }

    com_ptr<IMFSinkWriterEx> writer_ex;
    hr = writer->QueryInterface(IID_PPV_ARGS(writer_ex.put()));
    if (FAILED(hr)) {
        print_hr("required-IMFSinkWriterEx-query", hr);
        return false;
    }
    GUID category = GUID_NULL;
    com_ptr<IMFTransform> transform;
    hr = writer_ex->GetTransformForStream(stream_index, 0U, &category, transform.put());
    if (hr == MF_E_INVALIDINDEX && !transform) {
        std::cout << "SINKWRITER_TRANSFORMS stream=" << stream_index
            << " media=video enumerated=0 proof=GetTransformForStream(index=0)->MF_E_INVALIDINDEX\n";
    } else if (SUCCEEDED(hr)) {
        std::wstring friendly_name;
        const bool has_name = transform && get_transform_friendly_name(transform.get(), friendly_name);
        std::wcerr << L"FAIL stage=SinkWriter-video-transform-present index=0 category={"
            << std::hex << category.Data1 << L'-' << category.Data2 << L'-' << category.Data3
            << L'-' << static_cast<unsigned>(category.Data4[0]) << static_cast<unsigned>(category.Data4[1])
            << L'-' << static_cast<unsigned>(category.Data4[2]) << static_cast<unsigned>(category.Data4[3])
            << static_cast<unsigned>(category.Data4[4]) << static_cast<unsigned>(category.Data4[5])
            << static_cast<unsigned>(category.Data4[6]) << static_cast<unsigned>(category.Data4[7])
            << std::dec << L"} friendly_name=" << (has_name ? friendly_name : L"<unavailable>") << L'\n';
        return false;
    } else {
        print_hr("SinkWriter-video-transform-count-unproven", hr);
        return false;
    }
    size_t audio_transform_count = 0U;
    if (!prove_audio_sink_transforms(writer_ex.get(), audio_stream_index, audio_transform_count)) return false;

    for (size_t index = 0U; index < encoded.size(); ++index) {
        const auto& retained = encoded[index];
        if (!retained.sample) {
            std::cerr << "FAIL stage=retained-MFT-sample-missing before-write index=" << index << '\n';
            return false;
        }
        encoded_sample_facts before_write{};
        if (!snapshot_sample_facts(retained.sample.get(), before_write) ||
            !same_sample_facts(retained.facts, before_write)) {
            std::cerr << "FAIL stage=retained-MFT-sample-changed-before-write index=" << index << '\n';
            return false;
        }
        hr = writer->WriteSample(stream_index, retained.sample.get());
        if (FAILED(hr)) {
            std::cerr << "FAIL stage=SinkWriter-WriteSample index=" << index << ' ';
            print_hr("SinkWriter-WriteSample", hr);
            return false;
        }
    }
    LONGLONG previous_audio_time = 0;
    bool has_previous_audio_time = false;
    for (size_t index = 0U; index < audio_samples.size(); ++index) {
        const auto& retained = audio_samples[index];
        if (!retained.sample) {
            std::cerr << "FAIL stage=retained-PCM-sample-missing before-write index=" << index << '\n';
            return false;
        }
        pcm_sample_facts before_write{};
        if (!snapshot_pcm_sample_facts(retained.sample.get(), before_write) ||
            !same_pcm_sample_facts(retained.facts, before_write) ||
            (has_previous_audio_time && before_write.time_100ns < previous_audio_time)) {
            std::cerr << "FAIL stage=retained-PCM-sample-changed-or-reordered-before-write index="
                << index << '\n';
            return false;
        }
        previous_audio_time = before_write.time_100ns;
        has_previous_audio_time = true;
        hr = writer->WriteSample(audio_stream_index, retained.sample.get());
        if (FAILED(hr)) {
            std::cerr << "FAIL stage=SinkWriter-WriteSample-PCM index=" << index << ' ';
            print_hr("SinkWriter-WriteSample-PCM", hr);
            return false;
        }
    }
    hr = writer->Finalize();
    if (FAILED(hr)) {
        print_hr("SinkWriter-Finalize", hr);
        return false;
    }
    for (size_t index = 0U; index < encoded.size(); ++index) {
        encoded_sample_facts after_write{};
        if (!snapshot_sample_facts(encoded[index].sample.get(), after_write) ||
            !same_sample_facts(encoded[index].facts, after_write)) {
            std::cerr << "FAIL stage=SinkWriter-mutated-original-MFT-sample index=" << index << '\n';
            return false;
        }
    }
    for (size_t index = 0U; index < audio_samples.size(); ++index) {
        pcm_sample_facts after_write{};
        if (!snapshot_pcm_sample_facts(audio_samples[index].sample.get(), after_write) ||
            !same_pcm_sample_facts(audio_samples[index].facts, after_write)) {
            std::cerr << "FAIL stage=SinkWriter-mutated-original-PCM-sample index=" << index << '\n';
            return false;
        }
    }
    const uintmax_t output_size = std::filesystem::file_size(output_path, filesystem_error);
    if (filesystem_error || output_size == 0U) {
        std::cerr << "FAIL stage=finalized-MP4-output-size error="
            << (filesystem_error ? filesystem_error.message() : "empty-file") << '\n';
        return false;
    }
    std::cout << "SINKWRITER_MUX_GATE video_sample_objects=" << encoded.size()
        << " write_order=original-MFT-emission bytes_and_attributes=unchanged"
        << " video_transforms=0 audio_sample_objects=" << audio_samples.size()
        << " audio_timestamps_and_durations=unchanged audio_transforms=" << audio_transform_count
        << " audio_write_order=source-PCM-order finalized_mp4_bytes=" << output_size << '\n';
    return true;
}

bool inspect_native_mux_output(
    const std::filesystem::path& output_path,
    const lpb::media::isobmff_video_profile& source_profile,
    const isobmff_video_presentation_timeline& source_timeline,
    uint32_t expected_frame_count,
    bool expect_media_foundation_matrix_alias) {
    std::vector<uint8_t> bytes;
    if (!read_bytes(output_path, bytes)) {
        std::cerr << "FAIL stage=read-finalized-MP4-for-Native-inspection\n";
        return false;
    }
    lpb::memory_random_access memory{bytes};
    lpb::media::isobmff_video_profile profile{};
    lpb::media::video_profile_failure profile_failure{};
    if (!lpb::media::inspect_isobmff_video_profile(memory.view(), profile, profile_failure)) {
        std::cerr << "FAIL stage=Native-MP4-H264-profile-inspection profile_failure="
            << static_cast<unsigned>(profile_failure) << '\n';
        return false;
    }
    const auto is_codec = [](uint32_t value, const char a, const char b, const char c, const char d) {
        const uint32_t expected = (static_cast<uint32_t>(static_cast<uint8_t>(a)) << 24U) |
            (static_cast<uint32_t>(static_cast<uint8_t>(b)) << 16U) |
            (static_cast<uint32_t>(static_cast<uint8_t>(c)) << 8U) |
            static_cast<uint32_t>(static_cast<uint8_t>(d));
        return value == expected;
    };
    if ((!is_codec(profile.codec_fourcc, 'a', 'v', 'c', '1') &&
            !is_codec(profile.codec_fourcc, 'a', 'v', 'c', '3')) ||
        profile.classification != lpb::media::video_profile_class::ordinary_sdr ||
        profile.bit_depth_luma != 8U || profile.bit_depth_chroma != 8U) {
        std::cerr << "FAIL stage=Native-MP4-H264-8bit-SDR-profile\n";
        return false;
    }
    const bool color_facts_match = profile.full_range_known != 0U &&
        profile.full_range == source_profile.full_range &&
        profile.color_primaries == source_profile.color_primaries &&
        profile.transfer_characteristics == source_profile.transfer_characteristics &&
        (expect_media_foundation_matrix_alias
            ? source_profile.matrix_coefficients == 6U && profile.matrix_coefficients == 5U
            : profile.matrix_coefficients == source_profile.matrix_coefficients);
    if (!color_facts_match) {
        std::cerr << "FAIL stage=Native-MP4-CICP-raw-alias-or-normalized-profile"
            << " source=" << source_profile.color_primaries << '/'
            << source_profile.transfer_characteristics << '/' << source_profile.matrix_coefficients
            << " output=" << profile.color_primaries << '/' << profile.transfer_characteristics
            << '/' << profile.matrix_coefficients << " source_range="
            << static_cast<unsigned>(source_profile.full_range) << " output_range="
            << static_cast<unsigned>(profile.full_range) << '\n';
        return false;
    }
    isobmff_video_presentation_timeline output_timeline{};
    if (!inspect_isobmff_video_presentation_timeline(bytes, output_timeline)) {
        std::cerr << "FAIL stage=Native-MP4-presentation-timeline-inspection\n";
        return false;
    }
    if (output_timeline.sample_count != expected_frame_count ||
        output_timeline.visible_sample_count != expected_frame_count ||
        output_timeline.presentation_order.size() != expected_frame_count) {
        std::cerr << "FAIL stage=Native-MP4-visible-sample-count coded=" << output_timeline.sample_count
            << " visible=" << output_timeline.visible_sample_count
            << " expected=" << expected_frame_count << '\n';
        return false;
    }
    uint32_t failing_frame = 0U;
    if (!lpb::media::presentation_timing_within_source_frame_intervals(
            source_timeline, output_timeline, failing_frame)) {
        std::cerr << "FAIL stage=Native-MP4-frozen-source-frame-interval-rule frame="
            << failing_frame << '\n';
        return false;
    }
    std::cout << "NATIVE_MUX_INSPECTION raw_matrix_alias="
        << (expect_media_foundation_matrix_alias ? 1 : 0)
        << " codec=H264/8-bit/SDR CICP=" << profile.color_primaries << '/'
        << profile.transfer_characteristics << '/' << profile.matrix_coefficients
        << " full_range=" << static_cast<unsigned>(profile.full_range)
        << " coded="
        << output_timeline.sample_count << " visible=" << output_timeline.visible_sample_count
        << " frozen_source_frame_interval_rule=PASS\n";
    return true;
}

bool create_native_normalized_copy(
    const std::filesystem::path& raw_path,
    const std::filesystem::path& normalized_path,
    const lpb::media::isobmff_video_profile& source_profile) {
    std::vector<uint8_t> raw_bytes;
    if (!read_bytes(raw_path, raw_bytes)) {
        std::cerr << "FAIL stage=read-raw-A-V-MP4-for-CICP-normalization\n";
        return false;
    }
    lpb::memory_random_access raw_memory{raw_bytes};
    lpb::media::media_foundation_cicp_patch patch{};
    lpb::media::video_profile_failure failure{};
    lpb::media::video_profile_diagnostic diagnostic{};
    if (!lpb::media::prepare_media_foundation_cicp_patch(
            raw_memory.view(), source_profile, patch, failure, &diagnostic)) {
        std::cerr << "FAIL stage=prepare-Native-MF-CICP-normalization failure="
            << static_cast<unsigned>(failure) << " diagnostic="
            << static_cast<unsigned>(diagnostic) << '\n';
        return false;
    }
    if (!patch.changed || patch.moov_bytes.empty() ||
        patch.moov_offset > static_cast<uint64_t>(raw_bytes.size())) {
        std::cerr << "FAIL stage=Native-MF-CICP-patch-not-required-or-invalid\n";
        return false;
    }
    const size_t moov_offset = static_cast<size_t>(patch.moov_offset);
    if (patch.moov_bytes.size() > raw_bytes.size() - moov_offset) {
        std::cerr << "FAIL stage=Native-MF-CICP-patch-range-out-of-bounds\n";
        return false;
    }
    std::vector<uint8_t> normalized_bytes = raw_bytes;
    std::copy(patch.moov_bytes.begin(), patch.moov_bytes.end(),
        normalized_bytes.begin() + static_cast<std::ptrdiff_t>(moov_offset));
    if (normalized_bytes.size() != raw_bytes.size() ||
        !write_bytes_create_only(normalized_path, normalized_bytes)) {
        std::cerr << "FAIL stage=create-only-Native-normalized-A-V-MP4\n";
        return false;
    }
    std::vector<uint8_t> reread_bytes;
    if (!read_bytes(normalized_path, reread_bytes) || reread_bytes != normalized_bytes) {
        std::cerr << "FAIL stage=verify-create-only-Native-normalized-A-V-bytes\n";
        return false;
    }
    std::cout << "CICP_NORMALIZATION changed=1 raw_bytes=" << raw_bytes.size()
        << " normalized_bytes=" << reread_bytes.size()
        << " moov_offset=" << patch.moov_offset
        << " moov_bytes=" << patch.moov_bytes.size()
        << " raw_preserved=1 normalized_copy_create_only=1\n";
    return true;
}

bool run(
    const std::filesystem::path& source_path,
    const std::filesystem::path& raw_output_path,
    const std::filesystem::path& normalized_output_path) {
    lpb::media::isobmff_video_profile source_profile{};
    isobmff_video_presentation_timeline source_timeline{};
    if (!source_profile_and_timeline(source_path, source_profile, source_timeline)) return false;

    mf_lifetime mf{};
    if (FAILED(mf.co_result)) {
        print_hr("CoInitializeEx", mf.co_result);
        return false;
    }
    if (!mf.mf_initialized) {
        print_hr("MFStartup", mf.mf_result);
        return false;
    }

    std::vector<retained_pcm_sample> audio_samples;
    com_ptr<IMFMediaType> verified_pcm_type;
    if (!read_source_pcm_audio(source_path, audio_samples, verified_pcm_type)) return false;

    com_ptr<IMFAttributes> reader_attributes;
    HRESULT hr = MFCreateAttributes(reader_attributes.put(), 2U);
    if (SUCCEEDED(hr)) hr = reader_attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    if (SUCCEEDED(hr)) hr = reader_attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
    if (FAILED(hr)) {
        print_hr("configure-software-only-SourceReader", hr);
        return false;
    }
    com_ptr<IMFSourceReader> reader;
    hr = MFCreateSourceReaderFromURL(source_path.c_str(), reader_attributes.get(), reader.put());
    if (FAILED(hr)) {
        print_hr("MFCreateSourceReaderFromURL", hr);
        return false;
    }
    com_ptr<IMFMediaType> native_type;
    hr = reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0U, native_type.put());
    UINT32 width = 0U;
    UINT32 height = 0U;
    UINT32 fps_num = 0U;
    UINT32 fps_den = 0U;
    if (SUCCEEDED(hr)) hr = MFGetAttributeSize(native_type.get(), MF_MT_FRAME_SIZE, &width, &height);
    const HRESULT fps_hr = SUCCEEDED(hr)
        ? MFGetAttributeRatio(native_type.get(), MF_MT_FRAME_RATE, &fps_num, &fps_den) : hr;
    if (FAILED(hr) || width != 1920U || height != 1440U) {
        print_hr("validate-Apple-native-dimensions", FAILED(hr) ? hr : MF_E_INVALIDMEDIATYPE);
        return false;
    }
    if (FAILED(fps_hr)) {
        fps_num = 0U;
        fps_den = 0U;
    }
    if (fps_den == 0U) fps_den = 1U;

    com_ptr<IMFMediaType> reader_request;
    hr = MFCreateMediaType(reader_request.put());
    if (SUCCEEDED(hr)) hr = reader_request->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = reader_request->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(reader_request.get(), MF_MT_FRAME_SIZE, width, height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(reader_request.get(), MF_MT_PIXEL_ASPECT_RATIO, 1U, 1U);
    if (SUCCEEDED(hr)) hr = reader_request->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = reader->SetCurrentMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, reader_request.get());
    if (FAILED(hr)) {
        print_hr("configure-SourceReader-NV12-without-frame-rate", hr);
        return false;
    }
    com_ptr<IMFMediaType> reader_nv12_type;
    hr = reader->GetCurrentMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), reader_nv12_type.put());
    if (FAILED(hr) || !media_type_is_nv12(reader_nv12_type.get(), width, height)) {
        print_hr("validate-SourceReader-NV12-type", FAILED(hr) ? hr : MF_E_INVALIDMEDIATYPE);
        return false;
    }

    double fps_value = fps_num == 0U ? 30.0 : static_cast<double>(fps_num) / fps_den;
    if (fps_value <= 0.0 || fps_value > 240.0) fps_value = 30.0;
    double requested_bitrate = static_cast<double>(width) * height * fps_value * 0.12;
    requested_bitrate *= std::pow(2.0, (23.0 - 23.0) / 6.0);
    UINT32 bitrate = static_cast<UINT32>(requested_bitrate);
    bitrate = (std::max)(500000U, (std::min)(bitrate, 50000000U));

    MFT_REGISTER_TYPE_INFO input_filter{MFMediaType_Video, MFVideoFormat_NV12};
    MFT_REGISTER_TYPE_INFO output_filter{MFMediaType_Video, MFVideoFormat_H264};
    IMFActivate** activations = nullptr;
    UINT32 activation_count = 0U;
    hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_SYNCMFT,
        &input_filter, &output_filter, &activations, &activation_count);
    if (FAILED(hr)) {
        print_hr("MFTEnumEx-software-synchronous-video-encoders", hr);
        return false;
    }
    if (activation_count == 0U || activations == nullptr) {
        std::cerr << "BLOCKED stage=no-software-synchronous-H264-MFT enumerated; hardware enumeration was not requested\n";
        CoTaskMemFree(activations);
        return false;
    }
    std::cout << "MFT_ENUM software_sync_candidates=" << activation_count
        << " hardware_enumeration_requested=0 nominal_source_rate=" << fps_num << '/' << fps_den
        << " bitrate=" << bitrate << '\n';

    com_ptr<IMFTransform> encoder;
    std::wstring encoder_name;
    DWORD input_stream = 0U;
    DWORD output_stream = 0U;
    bool output_uses_rate = false;
    bool input_uses_rate = false;
    com_ptr<IMFMediaType> h264_type;
    for (UINT32 index = 0U; index < activation_count && !encoder; ++index) {
        std::wstring candidate_name;
        if (!get_friendly_name(activations[index], candidate_name)) candidate_name = L"<unnamed software MFT>";
        com_ptr<IMFTransform> candidate;
        hr = activations[index]->ActivateObject(IID_PPV_ARGS(candidate.put()));
        if (FAILED(hr)) {
            print_hr("activate-software-H264-MFT", hr);
            continue;
        }
        com_ptr<IMFAttributes> candidate_attributes;
        hr = candidate->GetAttributes(candidate_attributes.put());
        UINT32 asynchronous = FALSE;
        if (SUCCEEDED(hr) && SUCCEEDED(candidate_attributes->GetUINT32(MF_TRANSFORM_ASYNC, &asynchronous)) &&
            asynchronous != FALSE) {
            std::wcerr << L"MFT_CANDIDATE rejected async despite SYNCMFT enumeration: " << candidate_name << L'\n';
            continue;
        }
        if (!configure_encoder(candidate.get(), width, height,
                fps_num, fps_den, bitrate, input_stream, output_stream,
                output_uses_rate, input_uses_rate, h264_type)) {
            std::wcerr << L"MFT_CANDIDATE type negotiation rejected: " << candidate_name << L'\n';
            continue;
        }
        encoder_name = std::move(candidate_name);
        encoder = std::move(candidate);
    }
    for (UINT32 index = 0U; index < activation_count; ++index) {
        if (activations[index] != nullptr) activations[index]->Release();
    }
    CoTaskMemFree(activations);
    if (!encoder) {
        std::cerr << "BLOCKED stage=no-enumerated-software-H264-MFT-accepted-NV12-and-H264-types\n";
        return false;
    }
    std::wcout << L"MFT_SELECTED software_sync=1 name=" << encoder_name
        << L" input_nominal_rate_metadata=" << (input_uses_rate ? 1 : 0)
        << L" output_nominal_rate_metadata=" << (output_uses_rate ? 1 : 0)
        << L" sample_timestamps=Native-presentation-timeline\n";

    MFT_OUTPUT_STREAM_INFO output_info{};
    hr = encoder->GetOutputStreamInfo(output_stream, &output_info);
    if (FAILED(hr)) {
        print_hr("GetOutputStreamInfo", hr);
        return false;
    }
    std::vector<retained_encoded_sample> encoded;
    if (!run_encoder(encoder.get(), input_stream, output_stream, reader.get(), width, height, source_timeline,
            output_info, encoded)) return false;
    if (!verify_encoded_timing(source_timeline, encoded)) return false;
    com_ptr<IMFMediaType> final_h264_type;
    if (!get_final_h264_type(encoder.get(), output_stream, width, height, output_uses_rate, final_h264_type)) {
        return false;
    }
    if (!write_compressed_mux(
            raw_output_path, final_h264_type.get(), verified_pcm_type.get(), encoded, audio_samples)) return false;
    if (!inspect_native_mux_output(
            raw_output_path, source_profile, source_timeline,
            source_timeline.visible_sample_count, true)) return false;
    if (!create_native_normalized_copy(raw_output_path, normalized_output_path, source_profile)) return false;
    if (!inspect_native_mux_output(
            normalized_output_path, source_profile, source_timeline,
            source_timeline.visible_sample_count, false)) return false;
    std::cout << "PASS test-only direct software Media Foundation H.264 plus PCM-to-AAC A/V mux; "
        << "Native CICP normalization reread passed; production converter integration was not exercised.\n";
    return true;
}

bool allocate_output_attempt_directory(
    const std::filesystem::path& output_root,
    std::filesystem::path& attempt_directory) {
    std::error_code error;
    std::filesystem::create_directories(output_root, error);
    if (error || !std::filesystem::is_directory(output_root, error) || error) {
        std::cerr << "FAIL stage=create-or-validate-fixed-output-root error=" << error.value() << '\n';
        return false;
    }

    for (uint64_t attempt = 1U; attempt != 0U; ++attempt) {
        const std::filesystem::path candidate = output_root /
            (std::wstring(L"attempt-") + std::to_wstring(attempt));
        if (CreateDirectoryW(candidate.c_str(), nullptr) != 0) {
            attempt_directory = candidate;
            return true;
        }

        const DWORD error_code = GetLastError();
        if (error_code == ERROR_ALREADY_EXISTS || error_code == ERROR_FILE_EXISTS) {
            continue;
        }

        std::cerr << "FAIL stage=allocate-create-only-output-attempt error=" << error_code << '\n';
        return false;
    }

    std::cerr << "FAIL stage=output-attempt-identity-space-exhausted\n";
    return false;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) {
        std::cerr << "usage: lpb_mf_vfr_encoder_smoke <immutable-apple-mov> "
            << "<fixed-create-only-output-root>\n";
        return 2;
    }

    const std::filesystem::path output_root(argv[2]);
    std::filesystem::path attempt_directory;
    if (!allocate_output_attempt_directory(output_root, attempt_directory)) return 2;

    const std::filesystem::path raw_output_path = attempt_directory / L"apple-direct-mft-av-raw.mp4";
    const std::filesystem::path normalized_output_path = attempt_directory / L"apple-direct-mft-av-normalized.mp4";
    std::cout << "OUTPUT_IDENTITY root=" << output_root.string()
        << " attempt=" << attempt_directory.filename().string()
        << " raw=" << raw_output_path.string()
        << " normalized=" << normalized_output_path.string() << '\n';

    return run(std::filesystem::path(argv[1]), raw_output_path, normalized_output_path) ? 0 : 1;
}
