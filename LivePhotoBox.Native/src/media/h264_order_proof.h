#pragma once

#include <algorithm>
#include <cstdint>
#include <span>
#include <vector>
#include "media/video_sei_preservation.h"

namespace lpb::media {

// Narrow proof for the MF end-stamp correction, not a general AVC decoder.
// Only progressive Baseline, POC type 2, one reference I/P picture per AU,
// sequential frame_num and sliding-window reference marking are accepted.
// Everything else retains its original timestamps and faces the timing gate.
class h264_order_proof {
    struct bits {
        std::vector<uint8_t> bytes;
        size_t position{};
        bool init(std::span<const uint8_t> nal) {
            // All admitted SPS/PPS/slice header fields fit in this prefix.
            // Still validate EPB syntax over the entire NAL, but never copy
            // a potentially large compressed picture just to prove its order.
            bytes.reserve(64U);
            unsigned zeros = 0;
            for (size_t i = 1; i < nal.size(); ++i) {
                const uint8_t v = nal[i];
                if (zeros >= 2 && v == 3) {
                    if (++i >= nal.size() || nal[i] > 3) return false;
                    if (bytes.size() < 64U) bytes.push_back(nal[i]);
                    zeros = nal[i] == 0 ? 1U : 0U;
                } else {
                    if (zeros >= 2 && v <= 2) return false;
                    if (bytes.size() < 64U) bytes.push_back(v);
                    zeros = v == 0 ? zeros + 1 : 0;
                }
            }
            return !bytes.empty();
        }
        bool read(unsigned count, uint32_t& value) {
            if (count > 32 || position > bytes.size() * 8 || count > bytes.size() * 8 - position) return false;
            value = 0;
            for (unsigned i = 0; i < count; ++i, ++position)
                value = (value << 1) | ((bytes[position / 8] >> (7 - position % 8)) & 1U);
            return true;
        }
        bool ue(uint32_t& value) {
            unsigned zeros = 0; uint32_t bit = 0, suffix = 0;
            while (read(1, bit) && bit == 0) if (++zeros > 30) return false;
            if (bit == 0 || !read(zeros, suffix)) return false;
            value = ((1U << zeros) - 1) + suffix; return true;
        }
        bool se() { uint32_t v = 0; return ue(v); }
    };
    std::vector<uint8_t> sps_, pps_;
    uint32_t sps_id_{}, pps_id_{}, frame_bits_{}, previous_frame_{};
    bool started_{};
    bool parameter(std::span<const uint8_t> nal) {
        if (nal.size() < 2 || (nal[0] & 0x80U) || !(nal[0] & 0x60U)) return false;
        const unsigned type = nal[0] & 31U;
        auto& saved = type == 7 ? sps_ : pps_;
        if (!saved.empty()) return std::equal(saved.begin(), saved.end(), nal.begin(), nal.end());
        bits b; uint32_t v = 0, id = 0;
        if (!b.init(nal)) return false;
        if (type == 7) {
            if (!b.read(8,v) || v != 66 || !b.read(8,v) || (v & 3U) ||
                !b.read(8,v) || !b.ue(id) || id > 31 || !b.ue(v) || v > 12) return false;
            frame_bits_ = v + 4; sps_id_ = id;
            if (!b.ue(v) || v != 2 || !b.ue(v) || v > 16 || !b.read(1,v) || v != 0 ||
                !b.ue(v) || v > 65535 || !b.ue(v) || v > 65535 || !b.read(1,v) || v != 1) return false;
        } else if (type == 8) {
            if (sps_.empty() || !b.ue(id) || id > 255 || !b.ue(v) || v != sps_id_ ||
                !b.read(1,v) || v != 0 || !b.read(1,v) || v != 0 || !b.ue(v) || v != 0 ||
                !b.ue(v) || v > 31 || !b.ue(v) || v > 31 || !b.read(1,v) || v != 0 ||
                !b.read(2,v) || v != 0 || !b.se() || !b.se() || !b.se() ||
                !b.read(1,v) || !b.read(1,v) || !b.read(1,v) || v != 0) return false;
            pps_id_ = id;
        } else return false;
        saved.assign(nal.begin(), nal.end()); return true;
    }
    bool picture(std::span<const uint8_t> nal) {
        if (sps_.empty() || pps_.empty() || !(nal[0] & 0x60U)) return false;
        const bool idr = (nal[0] & 31U) == 5;
        bits b; uint32_t v = 0, slice = 0, frame = 0;
        if (!b.init(nal) || !b.ue(v) || v != 0 || !b.ue(slice) || slice > 9 ||
            (slice % 5 != 0 && slice % 5 != 2) || !b.ue(v) || v != pps_id_ ||
            !b.read(frame_bits_,frame)) return false;
        if (idr) {
            if (slice % 5 != 2 || frame != 0 || !b.ue(v) || v > 65535) return false;
        } else if (!started_ || frame != ((previous_frame_ + 1) % (1U << frame_bits_))) return false;
        if (slice % 5 == 0) {
            if (!b.read(1,v)) return false; // num_ref_idx_active_override_flag
            if (v && (!b.ue(v) || v > 31)) return false;
            if (!b.read(1,v) || v != 0) return false; // no list modification
        }
        if (idr) {
            if (!b.read(1,v) || (started_ && v != 0) || !b.read(1,v)) return false;
        } else if (!b.read(1,v) || v != 0) return false; // no MMCO, including POC reset
        previous_frame_ = frame; started_ = true; return true;
    }
    template<class Visit> static bool annex_b(std::span<const uint8_t> bytes, Visit visit) {
        auto prefix = [&](size_t at) -> size_t {
            if (at + 3 <= bytes.size() && bytes[at] == 0 && bytes[at+1] == 0) {
                if (bytes[at+2] == 1) return 3;
                if (at + 4 <= bytes.size() && bytes[at+2] == 0 && bytes[at+3] == 1) return 4;
            }
            return 0;
        };
        size_t pos = 0;
        while (pos < bytes.size()) {
            const size_t head = prefix(pos); if (!head) return false;
            const size_t start = pos + head; pos = start;
            while (pos < bytes.size() && !prefix(pos)) ++pos;
            size_t end = pos; while (end > start && bytes[end-1] == 0) --end;
            if (end == start || (bytes[start] & 0x80U) || !visit(bytes.subspan(start,end-start))) return false;
        }
        return !bytes.empty();
    }
public:
    bool configure(std::span<const uint8_t> bytes) {
        if (!annex_b(bytes,[&](auto nal) { return parameter(nal); })) return false;
        return !sps_.empty() && !pps_.empty();
    }
    bool access_unit(std::span<const uint8_t> bytes) {
        unsigned pictures = 0;
        return annex_b(bytes,[&](auto nal) {
            const unsigned type = nal[0] & 31U;
            if (type == 7 || type == 8) return parameter(nal);
            if (type == 1 || type == 5) return ++pictures == 1 && picture(nal);
            if (type == 6) {
                if (nal.size() > sei_access_unit_byte_budget) return false;
                const auto length = static_cast<uint32_t>(nal.size());
                std::vector<uint8_t> packet{static_cast<uint8_t>(length >> 24), static_cast<uint8_t>(length >> 16),
                    static_cast<uint8_t>(length >> 8), static_cast<uint8_t>(length)};
                packet.insert(packet.end(), nal.begin(), nal.end());
                std::vector<preserved_sei_message> messages;
                // In particular, pic_timing/pic_struct and unknown display
                // semantics must not authorize a source display-time rewrite.
                return collect_packet_sei(packet, 4U, false, messages, true) &&
                    std::all_of(messages.begin(), messages.end(), [](const auto& message) { return message.type == 5U; });
            }
            return type == 9 || type == 12;
        }) && pictures == 1;
    }
};
}
