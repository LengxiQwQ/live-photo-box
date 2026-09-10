#include "media/media_inspector.h"
#include "media/media_extractor.h"
#include "media/image_converter.h"
#include "media/video_converter.h"
#include "media/media_cleaner.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include <cctype>
#include <cstring>

using namespace lpb;
using namespace lpb::media;

namespace {
void classify_inspection_failure(lpb_context* context, lpb_result result) noexcept {
    if (!context) return;
    if (result == LPB_RESULT_OK) {
        set_inspection_status(context, LPB_INSPECTION_FAILURE_NONE, LPB_INSPECTION_STAGE_NONE);
        return;
    }
    // `inspect_source` records a typed stage/category at its decision point.
    // Never derive the public status by substring matching a diagnostic: that
    // makes ABI behavior depend on English wording and loses ambiguity vs
    // malformed semantics.
    lpb_inspection_status current{};
    current.struct_size = sizeof(current);
    if (lpb_get_last_inspection_status(context, &current) != LPB_RESULT_OK ||
        current.category == LPB_INSPECTION_FAILURE_NONE) {
        set_inspection_status(context,
            result == LPB_RESULT_INVALID_ARGUMENT ? LPB_INSPECTION_FAILURE_MALFORMED : LPB_INSPECTION_FAILURE_IO,
            LPB_INSPECTION_STAGE_READ);
    }
}

bool sha256_matches_hex(const uint8_t hash[32], const char* expected) noexcept {
    if (!expected) return false;
    for (size_t i = 0; i < 64; ++i) {
        const char c = expected[i];
        if (c == '\0') return false;
        const char want = (i % 2 == 0)
            ? "0123456789ABCDEF"[(hash[i / 2] >> 4) & 0x0F]
            : "0123456789ABCDEF"[hash[i / 2] & 0x0F];
        if (c != want && c != static_cast<char>(std::tolower(static_cast<unsigned char>(want)))) return false;
    }
    return expected[64] == '\0';
}

bool validate_gainmap_binding(lpb_context* context,
    const lpb_source_media_facts* facts) noexcept {
    if (!context || !facts || facts->struct_size < sizeof(lpb_source_media_facts)) {
        if (context) set_error(context, "GainMap binding validation received an incompatible source-facts struct.");
        return false;
    }
    if (facts->auxiliary_count > 8) {
        set_error(context, "Source facts contain more auxiliary items than the ABI capacity.");
        return false;
    }
    for (uint32_t i = 0; i < facts->auxiliary_count; ++i) {
        if (facts->auxiliary_items[i].struct_size < sizeof(lpb_auxiliary_item_facts)) {
            set_error(context, "Source facts contain an auxiliary item with an incompatible struct_size.");
            return false;
        }
    }
    if (!facts->gain_map.is_present) return true;
    if (facts->gain_map.struct_size < sizeof(lpb_gainmap_item_facts) ||
        facts->gain_map.file_range.length == 0 ||
        facts->gain_map.container == LPB_IMAGE_CONTAINER_UNKNOWN ||
        facts->gain_map.representation < LPB_AUX_REPRESENTATION_EMBEDDED ||
        facts->gain_map.representation > LPB_AUX_REPRESENTATION_MATERIALIZED ||
        facts->gain_map.ownership < LPB_AUX_OWNER_PRIMARY ||
        facts->gain_map.ownership > LPB_AUX_OWNER_AUXILIARY ||
        facts->gain_map.auxiliary_index >= facts->auxiliary_count ||
        facts->gain_map.relationship[0] == '\0') {
        set_error(context, "GainMap facts are missing a complete auxiliary identity.");
        return false;
    }
    const int32_t expected_owner_role = facts->gain_map.ownership == LPB_AUX_OWNER_PRIMARY
        ? LPB_ARTIFACT_PRIMARY_IMAGE : LPB_ARTIFACT_AUXILIARY_ITEM;
    if (facts->gain_map.owner_artifact_role != expected_owner_role) {
        set_error(context, "GainMap owner artifact role is inconsistent with ownership.");
        return false;
    }
    const auto& auxiliary = facts->auxiliary_items[facts->gain_map.auxiliary_index];
    const auto same_relationship = [](const char* left, const char* right) noexcept {
        const void* left_end = std::memchr(left, '\0', 64);
        const void* right_end = std::memchr(right, '\0', 64);
        if (!left_end || !right_end) return false;
        const size_t left_length = static_cast<const char*>(left_end) - left;
        const size_t right_length = static_cast<const char*>(right_end) - right;
        return left_length == right_length && std::memcmp(left, right, left_length) == 0;
    };
    if (!auxiliary.is_present ||
        auxiliary.container != facts->gain_map.container ||
        auxiliary.representation != facts->gain_map.representation ||
        auxiliary.ownership != facts->gain_map.ownership ||
        auxiliary.item_id != facts->gain_map.item_id ||
        auxiliary.file_range.offset != facts->gain_map.file_range.offset ||
        auxiliary.file_range.length != facts->gain_map.file_range.length ||
        !same_relationship(auxiliary.relationship, facts->gain_map.relationship)) {
        set_error(context, "GainMap facts are not bound to their referenced auxiliary entry.");
        return false;
    }
    return true;
}
}

extern "C" {

LPB_API lpb_result LPB_CALL lpb_inspect_media(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts)
{
    const lpb_result result = inspect_source(context, primary_path, secondary_path, out_facts);
    classify_inspection_failure(context, result);
    return result;
}

LPB_API lpb_result LPB_CALL lpb_inspect_media_with_plan(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    lpb_extraction_plan** out_plan,
    lpb_confirmed_residue* out_residues,
    size_t residues_capacity,
    size_t* out_residues_count,
    uint64_t* out_plan_generation)
{
    if (out_plan == nullptr || out_residues_count == nullptr || out_plan_generation == nullptr)
    {
        set_error(context, "Output extraction plan, generation, and residue count are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    *out_plan = nullptr;
    *out_residues_count = 0;
    *out_plan_generation = 0;

    lpb_context_operation context_operation(context);
    if (!context_operation.acquired())
    {
        set_error(context, "[AuthorityViolation] Native context is being destroyed.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    std::vector<lpb_confirmed_residue> residues;
    const lpb_result result = inspect_source_with_plan(
        context, primary_path, secondary_path, out_facts, out_plan, &residues,
        out_plan_generation);
    if (result != LPB_RESULT_OK)
    {
        classify_inspection_failure(context, result);
        return result;
    }

    *out_residues_count = residues.size();
    if (residues.size() > residues_capacity)
    {
        set_error(context, "The supplied residues buffer is too small for the issued extraction plan.");
        if (*out_plan != nullptr)
        {
            (void)lpb_release_extraction_plan(context, *out_plan);
            *out_plan = nullptr;
        }
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }
    if (out_residues != nullptr && !residues.empty())
    {
        std::memcpy(out_residues, residues.data(), residues.size() * sizeof(lpb_confirmed_residue));
    }
    classify_inspection_failure(context, LPB_RESULT_OK);
    return LPB_RESULT_OK;
}

LPB_API lpb_result LPB_CALL lpb_claim_extraction_plan(
    lpb_context* context,
    lpb_extraction_plan* plan,
    uint64_t generation)
{
    lpb_context_operation context_operation(context);
    if (!context_operation.acquired() || plan == nullptr || generation == 0)
    {
        set_error(context, "[AuthorityViolation] Context, plan, and generation are required to claim an extraction plan.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    try
    {
        const uint64_t token = plan_token_from_handle(plan);
        std::scoped_lock lock(context->plan_mutex);
        lpb_extraction_plan_record* record = find_plan_locked(context, token);
        if (record == nullptr || record->owner_context != context)
        {
            set_error(context, "[AuthorityViolation] Extraction plan token is not owned by this Native context.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }
        if (record->generation != generation || record->abi_version != LPB_NATIVE_ABI_VERSION ||
            record->plan_version != 1 || record->generation == 0)
        {
            set_error(context, "[AuthorityViolation] Extraction plan generation or metadata does not match the issued authority.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }
        if (record->state != lpb_plan_state::Issued || record->native_call_active)
        {
            set_error(context, "[PlanReplayed] Extraction plan is already claimed, released, or consumed.");
            return LPB_RESULT_PLAN_REPLAYED;
        }
        record->state = lpb_plan_state::Claimed;
        record->managed_claim_active = true;
#if defined(LPB_NATIVE_TEST_HARNESS)
        ++context->test_claimed;
#endif
        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "[InternalError] Failed to claim the extraction authority plan.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

LPB_API lpb_result LPB_CALL lpb_finish_extraction_plan(
    lpb_context* context,
    lpb_extraction_plan* plan,
    uint64_t generation)
{
    lpb_context_operation context_operation(context);
    if (!context_operation.acquired() || plan == nullptr || generation == 0)
    {
        set_error(context, "[AuthorityViolation] Context, plan, and generation are required to finish an extraction plan.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    try
    {
        const uint64_t token = plan_token_from_handle(plan);
        std::scoped_lock lock(context->plan_mutex);
        lpb_extraction_plan_record* record = find_plan_locked(context, token);
        if (record == nullptr || record->owner_context != context)
        {
            set_error(context, "[AuthorityViolation] Extraction plan token is not owned by this Native context.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }
        if (record->generation != generation)
        {
            set_error(context, "[AuthorityViolation] Extraction plan generation does not match the issued authority.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }
        if (record->native_call_active)
        {
            set_error(context, "[PlanReplayed] Cannot finish an extraction plan while its native operation is active.");
            return LPB_RESULT_PLAN_REPLAYED;
        }
        record->managed_claim_active = false;
        if (record->state == lpb_plan_state::Claimed || record->state == lpb_plan_state::ReleaseRequested)
        {
            const bool release_requested = record->state == lpb_plan_state::ReleaseRequested;
            record->state = release_requested ? lpb_plan_state::Released : lpb_plan_state::Consumed;
#if defined(LPB_NATIVE_TEST_HARNESS)
            if (release_requested)
            {
                ++context->test_released;
            }
            else
            {
                ++context->test_consumed;
            }
#endif
        }
        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "[InternalError] Failed to finish the extraction authority plan.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

LPB_API lpb_result LPB_CALL lpb_release_extraction_plan(
    lpb_context* context,
    lpb_extraction_plan* plan)
{
    if (context == nullptr || plan == nullptr)
    {
        set_error(context, "Context and extraction plan are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    lpb_context_operation context_operation(context);
    if (!context_operation.acquired())
    {
        set_error(context, "[AuthorityViolation] Native context is being destroyed.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    try
    {
        const uint64_t token = plan_token_from_handle(plan);
        std::scoped_lock lock(context->plan_mutex);
        lpb_extraction_plan_record* record = find_plan_locked(context, token);
        if (record == nullptr || record->owner_context != context)
        {
            set_error(context, "[AuthorityViolation] Extraction plan token is not owned by this Native context.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }
        if (record->state == lpb_plan_state::Issued)
        {
            record->state = lpb_plan_state::Released;
#if defined(LPB_NATIVE_TEST_HARNESS)
            ++context->test_released;
#endif
        }
        else if (record->state == lpb_plan_state::Claimed)
        {
            record->state = (record->managed_claim_active || record->native_call_active)
                ? lpb_plan_state::ReleaseRequested
                : lpb_plan_state::Released;
        }
        else if (record->state == lpb_plan_state::Consumed)
        {
            record->state = lpb_plan_state::Released;
#if defined(LPB_NATIVE_TEST_HARNESS)
            ++context->test_released;
#endif
        }
        // ReleaseRequested and Released are idempotent.  A native call that
        // is still active retains the record until its operation lease exits.
        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "[InternalError] Failed to release the extraction authority plan.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

LPB_API lpb_result LPB_CALL lpb_inspect_media_with_residues(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    lpb_confirmed_residue* out_residues,
    size_t residues_capacity,
    size_t* out_residues_count)
{
    std::vector<lpb_confirmed_residue> residues;
    lpb_result res = inspect_source(context, primary_path, secondary_path, out_facts, &residues);
    if (res != LPB_RESULT_OK) { classify_inspection_failure(context, res); return res; }

    if (residues.size() > residues_capacity) {
        if (out_residues_count) *out_residues_count = residues.size();
        set_error(context, "The supplied residues buffer is too small.");
        classify_inspection_failure(context, LPB_RESULT_BUFFER_TOO_SMALL);
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }

    if (out_residues && residues_capacity > 0) {
        for (size_t i = 0; i < residues.size(); ++i) {
            out_residues[i] = residues[i];
        }
    }
    if (out_residues_count) *out_residues_count = residues.size();
    classify_inspection_failure(context, LPB_RESULT_OK);
    return LPB_RESULT_OK;
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
    (void)primary_path;
    (void)secondary_path;
    (void)facts;
    (void)output_image_path;
    (void)output_video_path;
    (void)output_gainmap_path;
    lpb_context_operation context_operation(context);
    if (context != nullptr && !context_operation.acquired())
    {
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
    set_error(context, "[AuthorityViolation] Raw facts-based extraction is disabled; use an Inspector-issued extraction plan.");
    return LPB_RESULT_AUTHORITY_VIOLATION;
}

#if defined(LPB_NATIVE_TEST_HARNESS)
LPB_API lpb_result LPB_CALL lpb_test_extract_media_from_facts(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path)
{
    lpb_context_operation context_operation(context);
    if (!context_operation.acquired())
    {
        set_error(context, "[AuthorityViolation] Native context is unavailable for the test harness extraction.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
    if (!validate_gainmap_binding(context, facts)) return LPB_RESULT_INVALID_ARGUMENT;
    return extract_source_internal(
        context, primary_path, secondary_path, facts,
        output_image_path, output_video_path, output_gainmap_path, nullptr, 0);
}

LPB_API lpb_result LPB_CALL lpb_test_extract_media_from_facts_outputs(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path,
    const lpb_extraction_output* auxiliary_outputs,
    size_t auxiliary_output_count)
{
    lpb_context_operation context_operation(context);
    if (!context_operation.acquired())
    {
        set_error(context, "[AuthorityViolation] Native context is unavailable for the test harness extraction.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
    if (!validate_gainmap_binding(context, facts)) return LPB_RESULT_INVALID_ARGUMENT;
    return extract_source_internal(
        context, primary_path, secondary_path, facts,
        output_image_path, output_video_path, output_gainmap_path,
        nullptr, nullptr, auxiliary_outputs, auxiliary_output_count);
}

/* Test-harness-only introspection used to prove that the public token is not
   the address of the authoritative registry record.  This symbol is absent
   from every production configuration. */
LPB_API uintptr_t LPB_CALL lpb_test_get_extraction_plan_record_address(
    lpb_context* context,
    lpb_extraction_plan* plan)
{
    lpb_context_operation context_operation(context);
    if (!context_operation.acquired() || plan == nullptr)
    {
        return 0;
    }
    std::scoped_lock lock(context->plan_mutex);
    lpb_extraction_plan_record* record = find_plan_locked(context, plan_token_from_handle(plan));
    return record == nullptr || record->owner_context != context
        ? 0
        : reinterpret_cast<uintptr_t>(record);
}

LPB_API uint64_t LPB_CALL lpb_test_get_context_id(lpb_context* context)
{
    lpb_context_operation context_operation(context);
    return context_operation.acquired() ? test_context_id(context) : 0;
}

LPB_API lpb_result LPB_CALL lpb_test_get_plan_accounting(
    lpb_context* context,
    lpb_test_plan_accounting* out_accounting)
{
    if (out_accounting == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    *out_accounting = {};

    lpb_context_operation context_operation(context);
    if (!context_operation.acquired())
    {
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
    return test_get_plan_accounting(context, *out_accounting)
        ? LPB_RESULT_OK
        : LPB_RESULT_INTERNAL_ERROR;
}

LPB_API lpb_result LPB_CALL lpb_test_get_destroyed_plan_accounting(
    uint64_t context_id,
    lpb_test_plan_accounting* out_accounting)
{
    if (out_accounting == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    *out_accounting = {};
    return test_get_destroyed_plan_accounting(context_id, *out_accounting)
        ? LPB_RESULT_OK
        : LPB_RESULT_AUTHORITY_VIOLATION;
}

LPB_API lpb_result LPB_CALL lpb_test_probe_destroyed_plan(
    uint64_t context_id,
    uint64_t plan_token)
{
    return test_probe_destroyed_plan(context_id, plan_token);
}
#endif

LPB_API lpb_result LPB_CALL lpb_extract_media_with_plan(
    lpb_context* context,
    lpb_extraction_plan* plan,
    const char* primary_path,
    const char* secondary_path,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path)
{
    return extract_source_with_plan(
        context, plan, primary_path, secondary_path,
        output_image_path, output_video_path, output_gainmap_path,
        nullptr, nullptr, 0);
}

LPB_API lpb_result LPB_CALL lpb_extract_media_with_plan_outputs(
    lpb_context* context,
    lpb_extraction_plan* plan,
    const char* primary_path,
    const char* secondary_path,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path,
    const lpb_extraction_output* auxiliary_outputs,
    size_t auxiliary_output_count)
{
    return extract_source_with_plan(
        context, plan, primary_path, secondary_path,
        output_image_path, output_video_path, output_gainmap_path,
        nullptr, auxiliary_outputs, auxiliary_output_count);
}

LPB_API lpb_result LPB_CALL lpb_extract_media_with_plan_outputs_v2(
    lpb_context* context,
    lpb_extraction_plan* plan,
    const char* primary_path,
    const char* secondary_path,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path,
    const char* cleanup_source_path,
    const lpb_extraction_output* auxiliary_outputs,
    size_t auxiliary_output_count)
{
    return extract_source_with_plan(
        context, plan, primary_path, secondary_path,
        output_image_path, output_video_path, output_gainmap_path,
        cleanup_source_path, auxiliary_outputs, auxiliary_output_count);
}

LPB_API lpb_result LPB_CALL lpb_rollback_extraction_outputs(
    lpb_context* context,
    lpb_extraction_plan* plan,
    uint64_t generation)
{
    return rollback_extraction_outputs_with_plan(context, plan, generation);
}

LPB_API lpb_result LPB_CALL lpb_verify_extraction_outputs(
    lpb_context* context,
    lpb_extraction_plan* plan,
    uint64_t generation)
{
    return verify_extraction_outputs_with_plan(context, plan, generation);
}

#if defined(LPB_NATIVE_TEST_HARNESS)
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

LPB_API lpb_result LPB_CALL lpb_test_set_cleaner_snapshot_hook(
    lpb_context* context,
    lpb_cleaner_snapshot_callback callback,
    void* user_data)
{
    if (!context) return LPB_RESULT_INVALID_ARGUMENT;
    context->cleaner_post_snapshot_callback = callback;
    context->cleaner_callback_user_data = user_data;
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
#endif


LPB_API lpb_result LPB_CALL lpb_clean_source_protocol_with_plan(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const lpb_cleanup_action* actions,
    size_t action_count,
    const lpb_cleanup_artifact_binding* targets,
    size_t target_count,
    const char* input_image_path,
    const char* input_video_path,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count)
{
    if (!validate_gainmap_binding(context, facts)) {
        if (out_facts_count) *out_facts_count = 0;
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    return clean_source_protocol_with_plan(
        context, facts, actions, action_count,
        targets, target_count,
        input_image_path, input_video_path,
        nullptr, nullptr,
        output_image_path, output_video_path,
        out_facts, facts_capacity, out_facts_count);
}

LPB_API lpb_result LPB_CALL lpb_clean_source_protocol_with_plan_and_cleanup_source(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const lpb_cleanup_action* actions,
    size_t action_count,
    const lpb_cleanup_artifact_binding* targets,
    size_t target_count,
    const char* input_image_path,
    const char* input_video_path,
    const char* cleanup_source_path,
    const lpb_cleanup_artifact_binding* cleanup_source_target,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count)
{
    if (!validate_gainmap_binding(context, facts)) {
        if (out_facts_count) *out_facts_count = 0;
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    return clean_source_protocol_with_plan(
        context, facts, actions, action_count,
        targets, target_count,
        input_image_path, input_video_path,
        cleanup_source_path, cleanup_source_target,
        output_image_path, output_video_path,
        out_facts, facts_capacity, out_facts_count);
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

LPB_API lpb_result LPB_CALL lpb_reassemble_jpeg_gainmap(
    lpb_context* context,
    const char* primary_jpeg_path,
    const char* gainmap_jpeg_path,
    const char* output_path,
    const char* expected_gainmap_sha256)
{
    if (!context || !primary_jpeg_path || !gainmap_jpeg_path || !output_path || !expected_gainmap_sha256) {
        if (context) set_error(context, "Invalid arguments for GainMap JPEG reassembly.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
        set_error(context, "GainMap reassembly cancelled.");
        return LPB_RESULT_CANCELLED;
    }

    if (paths_alias(primary_jpeg_path, output_path) || paths_alias(gainmap_jpeg_path, output_path)) {
        set_error(context, "Output path must not match input paths.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto p_primary = utf8_to_path(primary_jpeg_path);
    auto p_gainmap = utf8_to_path(gainmap_jpeg_path);
    auto p_output = utf8_to_path(output_path);

    HANDLE h_primary = CreateFileW(
        p_primary.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (h_primary == INVALID_HANDLE_VALUE) {
        set_error(context, "Failed to open primary JPEG file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    uint8_t magic[2]{};
    DWORD bytes_read = 0;
    if (!ReadFile(h_primary, magic, 2, &bytes_read, NULL) || bytes_read < 2 || magic[0] != 0xFF || magic[1] != 0xD8) {
        CloseHandle(h_primary);
        set_error(context, "Primary image is not a valid JPEG file (missing SOI marker).");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    SetFilePointer(h_primary, 0, NULL, FILE_BEGIN);

    HANDLE h_gainmap = CreateFileW(
        p_gainmap.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (h_gainmap == INVALID_HANDLE_VALUE) {
        CloseHandle(h_primary);
        set_error(context, "Failed to open gainmap JPEG file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    bytes_read = 0;
    if (!ReadFile(h_gainmap, magic, 2, &bytes_read, NULL) || bytes_read < 2 || magic[0] != 0xFF || magic[1] != 0xD8) {
        CloseHandle(h_primary);
        CloseHandle(h_gainmap);
        set_error(context, "GainMap image is not a valid JPEG file (missing SOI marker).");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    SetFilePointer(h_gainmap, 0, NULL, FILE_BEGIN);

    HANDLE h_out = CreateFileW(
        p_output.c_str(),
        GENERIC_WRITE,
        0,
        NULL,
        CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (h_out == INVALID_HANDLE_VALUE) {
        DWORD err = GetLastError();
        CloseHandle(h_primary);
        CloseHandle(h_gainmap);
        if (err == ERROR_FILE_EXISTS || err == ERROR_ALREADY_EXISTS) {
            set_error(context, "Output file already exists.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        set_error(context, "Failed to create output file for GainMap reassembly.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    std::vector<uint8_t> buffer(64 * 1024);
    lpb::crypto::sha256_ctx gainmap_sha;
    bool failed = false;
    bool cancelled = false;
    bool identity_mismatch = false;

    while (ReadFile(h_primary, buffer.data(), static_cast<DWORD>(buffer.size()), &bytes_read, NULL) && bytes_read > 0) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            failed = true;
            cancelled = true;
            break;
        }
        DWORD bytes_written = 0;
        if (!WriteFile(h_out, buffer.data(), bytes_read, &bytes_written, NULL) || bytes_written != bytes_read) {
            failed = true;
            break;
        }
    }

    if (!failed) {
        while (ReadFile(h_gainmap, buffer.data(), static_cast<DWORD>(buffer.size()), &bytes_read, NULL) && bytes_read > 0) {
            if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                failed = true;
                cancelled = true;
                break;
            }
            DWORD bytes_written = 0;
            if (!WriteFile(h_out, buffer.data(), bytes_read, &bytes_written, NULL) || bytes_written != bytes_read) {
                failed = true;
                break;
            }
            gainmap_sha.update(buffer.data(), bytes_read);
        }
    }

    if (!failed) {
        uint8_t actual_gainmap_sha[32]{};
        gainmap_sha.finalize(actual_gainmap_sha);
        if (!sha256_matches_hex(actual_gainmap_sha, expected_gainmap_sha256)) {
            failed = true;
            identity_mismatch = true;
        }
    }

    if (!failed) {
        if (!FlushFileBuffers(h_out)) {
            failed = true;
        }
    }

    CloseHandle(h_primary);
    CloseHandle(h_gainmap);
    CloseHandle(h_out);

    if (failed) {
        DeleteFileW(p_output.c_str());
        if (cancelled) {
            set_error(context, "GainMap reassembly cancelled.");
            return LPB_RESULT_CANCELLED;
        }
        set_error(context, identity_mismatch
            ? "GainMap artifact identity changed before or during final consumption."
            : "Failed during GainMap JPEG reassembly write.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

}


