#pragma once

#include "livephotobox_native.h"

namespace lpb::media {

// Internal deployment facts; not a public C ABI or request-specific promise.
enum class mf_deployment_state { available, component_missing, encoder_absent, probe_failure };
struct mf_deployment_probe {
    mf_deployment_state state{mf_deployment_state::probe_failure};
    uint32_t error_code{};
    const char* reason{"MFProbeFailure: deployment query did not complete."};
    bool permits_h264_fallback() const noexcept {
        return state == mf_deployment_state::component_missing || state == mf_deployment_state::encoder_absent;
    }
};
mf_deployment_probe probe_software_video_encoder(lpb_context* context, lpb_video_codec codec) noexcept;
// Only an exact required-file open error can establish component absence.
mf_deployment_state classify_mf_component_open_error(uint32_t error) noexcept;
#if defined(LPB_P8_MF_TEST_SEAM)
// Compiled into the standalone synthetic smoke executable only. No DLL export.
void set_synthetic_mf_probe(mf_deployment_state state) noexcept;
void clear_synthetic_mf_probe() noexcept;
#endif

// Deployment availability only: enumerate/activate approved synchronous software
// NV12 encoders. Request-specific type negotiation remains in conversion.
bool software_video_encoder_available(lpb_context* context, lpb_video_codec codec) noexcept;

lpb_result probe_video_file(
    lpb_context* context,
    const char* video_path,
    lpb_video_item_facts* out_video_facts) noexcept;

lpb_result remux_video_file(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container,
    lpb_video_preservation_result_v1* out_preservation = nullptr,
    lpb_video_preservation_result_v2* out_complete_proof = nullptr) noexcept;

lpb_result transcode_video_file(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    char* out_encoder_used,
    size_t encoder_buf_len,
    lpb_video_backend_diagnostics* out_diagnostics = nullptr,
    lpb_video_preservation_result_v1* out_preservation = nullptr,
    lpb_video_preservation_result_v2* out_complete_proof = nullptr) noexcept;

} // namespace lpb::media
