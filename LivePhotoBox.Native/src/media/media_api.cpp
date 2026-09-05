#include "media/media_inspector.h"
#include "media/media_extractor.h"
#include "media/image_converter.h"
#include "media/video_converter.h"
#include "media/media_cleaner.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"

using namespace lpb;
using namespace lpb::media;

extern "C" {

LPB_API lpb_result LPB_CALL lpb_inspect_media(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts)
{
    return inspect_source(context, primary_path, secondary_path, out_facts);
}

LPB_API lpb_result LPB_CALL lpb_extract_media(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path)
{
    return extract_source(context, primary_path, secondary_path, facts, output_image_path, output_video_path, output_gainmap_path);
}

LPB_API lpb_result LPB_CALL lpb_test_set_extractor_fault(
    lpb_context* context,
    lpb_extractor_fault fault,
    int32_t target_artifact,
    uint64_t trigger_after_bytes,
    lpb_extractor_step_callback callback,
    void* user_data)
{
    if (!context) return LPB_RESULT_INVALID_ARGUMENT;
    context->extractor_hook.fault = fault;
    context->extractor_hook.target_artifact = target_artifact;
    context->extractor_hook.trigger_after_bytes = trigger_after_bytes;
    context->extractor_hook.step_callback = callback;
    context->extractor_hook.callback_user_data = user_data;
    return LPB_RESULT_OK;
}

LPB_API lpb_result LPB_CALL lpb_test_sha256_buffer(
    const uint8_t* data,
    size_t length,
    uint8_t out_hash[32])
{
    if (!out_hash || (!data && length > 0)) return LPB_RESULT_INVALID_ARGUMENT;
    lpb::crypto::sha256_buffer(data, length, out_hash);
    return LPB_RESULT_OK;
}

LPB_API lpb_result LPB_CALL lpb_test_sha256_file(
    void* file_handle,
    uint8_t out_hash[32])
{
    if (!file_handle || file_handle == INVALID_HANDLE_VALUE || !out_hash) return LPB_RESULT_INVALID_ARGUMENT;
    if (!lpb::crypto::sha256_file(static_cast<HANDLE>(file_handle), out_hash)) {
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

LPB_API lpb_result LPB_CALL lpb_clean_source_protocol(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const char* input_image_path,
    const char* input_video_path,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count)
{
    return clean_source_protocol(context, facts, input_image_path, input_video_path, output_image_path, output_video_path, out_facts, facts_capacity, out_facts_count);
}

LPB_API lpb_result LPB_CALL lpb_probe_video(
    lpb_context* context,
    const char* video_path,
    lpb_video_item_facts* out_video_facts)
{
    return probe_video_file(context, video_path, out_video_facts);
}

LPB_API lpb_result LPB_CALL lpb_remux_video(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container)
{
    return remux_video_file(context, input_video_path, output_video_path, target_container);
}

LPB_API lpb_result LPB_CALL lpb_convert_image(
    lpb_context* context,
    const char* input_image_path,
    const char* output_image_path,
    lpb_image_container target_container,
    int32_t quality,
    int32_t* out_reencoded)
{
    return convert_image_file(context, input_image_path, output_image_path, target_container, quality, out_reencoded);
}

LPB_API lpb_result LPB_CALL lpb_transcode_video(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    char* out_encoder_used,
    size_t encoder_buf_len)
{
    return transcode_video_file(context, input_video_path, output_video_path, target_container, target_codec, crf, out_encoder_used, encoder_buf_len);
}

}

