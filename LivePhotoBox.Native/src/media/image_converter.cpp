#include "media/image_converter.h"
#include "media/media_inspector.h"
#include "foundation/internal.h"
#include <fstream>
#include <filesystem>
#include <windows.h>
#include <wincodec.h>
#include <wincodecsdk.h>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

namespace fs = std::filesystem;

namespace lpb::media {

static lpb_result fast_file_copy(lpb_context* context, const char* in_path, const char* out_path) {
    if (!in_path || !out_path) return LPB_RESULT_INVALID_ARGUMENT;
    auto p_in = utf8_to_path(in_path);
    auto p_out = utf8_to_path(out_path);
    std::error_code ec;
    fs::copy_file(p_in, p_out, fs::copy_options::overwrite_existing, ec);
    if (ec) {
        set_error(context, "Failed to copy image file.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

lpb_result convert_image_file(
    lpb_context* context,
    const char* input_image_path,
    const char* output_image_path,
    lpb_image_container target_container,
    int32_t quality,
    int32_t* out_reencoded) noexcept
{
    if (!input_image_path || !output_image_path) {
        set_error(context, "Invalid arguments for image conversion.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto p_in = utf8_to_path(input_image_path);
    std::ifstream in(p_in, std::ios::binary);
    if (!in.is_open()) {
        set_error(context, "Cannot open input image file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    uint8_t header[16] = {0};
    in.read(reinterpret_cast<char*>(header), sizeof(header));
    in.close();

    lpb_image_container src_cont = detect_image_container(std::span<const uint8_t>(header, sizeof(header)));

    // 1. Same container format -> Direct structure copy (no loss, no re-encoding)
    if (src_cont == target_container && target_container != LPB_IMAGE_CONTAINER_UNKNOWN) {
        if (out_reencoded) *out_reencoded = 0;
        return fast_file_copy(context, input_image_path, output_image_path);
    }

    // 2. Cross-container conversion using Windows Imaging Component (WIC)
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool co_initialized = SUCCEEDED(hr);

    IWICImagingFactory* factory = nullptr;
    hr = CoCreateInstance(
        CLSID_WICImagingFactory,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory));

    if (FAILED(hr) || !factory) {
        if (co_initialized) CoUninitialize();
        set_error(context, "WIC Imaging Factory creation failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    int in_len = MultiByteToWideChar(CP_UTF8, 0, input_image_path, -1, nullptr, 0);
    int out_len = MultiByteToWideChar(CP_UTF8, 0, output_image_path, -1, nullptr, 0);
    std::wstring w_in(in_len, 0);
    std::wstring w_out(out_len, 0);
    MultiByteToWideChar(CP_UTF8, 0, input_image_path, -1, w_in.data(), in_len);
    MultiByteToWideChar(CP_UTF8, 0, output_image_path, -1, w_out.data(), out_len);

    IWICBitmapDecoder* decoder = nullptr;
    hr = factory->CreateDecoderFromFilename(
        w_in.c_str(),
        nullptr,
        GENERIC_READ,
        WICDecodeMetadataCacheOnDemand,
        &decoder);

    if (FAILED(hr) || !decoder) {
        factory->Release();
        if (co_initialized) CoUninitialize();
        set_error(context, "Failed to create WIC decoder for input image.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    IWICBitmapFrameDecode* frame_decode = nullptr;
    hr = decoder->GetFrame(0, &frame_decode);
    if (FAILED(hr) || !frame_decode) {
        decoder->Release();
        factory->Release();
        if (co_initialized) CoUninitialize();
        set_error(context, "Failed to get image frame from decoder.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    // Determine target WIC container GUID
    GUID target_guid = GUID_ContainerFormatJpeg;
    if (target_container == LPB_IMAGE_CONTAINER_PNG) {
        target_guid = GUID_ContainerFormatPng;
    } else if (target_container == LPB_IMAGE_CONTAINER_HEIC) {
        target_guid = GUID_ContainerFormatHeif;
    }

    IWICStream* stream = nullptr;
    hr = factory->CreateStream(&stream);
    if (SUCCEEDED(hr)) {
        hr = stream->InitializeFromFilename(w_out.c_str(), GENERIC_WRITE);
    }

    IWICBitmapEncoder* encoder = nullptr;
    if (SUCCEEDED(hr)) {
        hr = factory->CreateEncoder(target_guid, nullptr, &encoder);
    }
    if (SUCCEEDED(hr) && encoder) {
        hr = encoder->Initialize(stream, WICBitmapEncoderNoCache);
    }

    if (FAILED(hr) || !encoder) {
        if (stream) stream->Release();
        frame_decode->Release();
        decoder->Release();
        factory->Release();
        if (co_initialized) CoUninitialize();
        set_error(context, "Failed to initialize WIC encoder for target format.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    IWICBitmapFrameEncode* frame_encode = nullptr;
    IPropertyBag2* prop_bag = nullptr;
    hr = encoder->CreateNewFrame(&frame_encode, &prop_bag);
    if (SUCCEEDED(hr) && prop_bag && target_container == LPB_IMAGE_CONTAINER_JPEG) {
        PROPBAG2 opt = {0};
        opt.pstrName = const_cast<LPOLESTR>(L"ImageQuality");
        VARIANT var;
        VariantInit(&var);
        var.vt = VT_R4;
        var.fltVal = quality > 0 ? static_cast<float>(quality) / 100.0f : 0.9f;
        prop_bag->Write(1, &opt, &var);
    }

    if (SUCCEEDED(hr)) {
        hr = frame_encode->Initialize(prop_bag);
    }

    // Preserve color contexts (ICC profiles / wide color gamut) if available
    if (SUCCEEDED(hr)) {
        UINT cColorContexts = 0;
        frame_decode->GetColorContexts(0, nullptr, &cColorContexts);
        if (cColorContexts > 0) {
            std::vector<IWICColorContext*> colorContexts(cColorContexts, nullptr);
            for (UINT i = 0; i < cColorContexts; ++i) {
                factory->CreateColorContext(&colorContexts[i]);
            }
            if (SUCCEEDED(frame_decode->GetColorContexts(cColorContexts, colorContexts.data(), &cColorContexts))) {
                frame_encode->SetColorContexts(cColorContexts, colorContexts.data());
            }
            for (auto* pCtx : colorContexts) {
                if (pCtx) pCtx->Release();
            }
        }
    }

    // Best-effort metadata block copying (Exif, GPS, XMP) across compatible container encoders
    if (SUCCEEDED(hr)) {
        IWICMetadataBlockReader* pBlockReader = nullptr;
        IWICMetadataBlockWriter* pBlockWriter = nullptr;
        if (SUCCEEDED(frame_decode->QueryInterface(IID_PPV_ARGS(&pBlockReader)))) {
            if (SUCCEEDED(frame_encode->QueryInterface(IID_PPV_ARGS(&pBlockWriter)))) {
                pBlockWriter->InitializeFromBlockReader(pBlockReader);
                pBlockWriter->Release();
            }
            pBlockReader->Release();
        }
    }

    if (SUCCEEDED(hr)) {
        hr = frame_encode->WriteSource(frame_decode, nullptr);
    }
    if (SUCCEEDED(hr)) {
        hr = frame_encode->Commit();
    }
    if (SUCCEEDED(hr)) {
        hr = encoder->Commit();
    }

    if (prop_bag) prop_bag->Release();
    if (frame_encode) frame_encode->Release();
    encoder->Release();
    stream->Release();
    frame_decode->Release();
    decoder->Release();
    factory->Release();
    if (co_initialized) CoUninitialize();

    if (FAILED(hr)) {
        set_error(context, "WIC image encoding failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (out_reencoded) *out_reencoded = 1;
    return LPB_RESULT_OK;
}

} // namespace lpb::media
