// Included in media_inspector.cpp: reuses owned Exif/XMP parsers. Discovery only.
lpb_result discover_media_candidate_v2(lpb_context* context, const char* path, uint32_t capability,
    lpb_media_candidate_v1* out) noexcept {
    if (!context || !path || !out || out->struct_size != sizeof(*out) || out->api_version != 2 ||
        (capability != 1 && capability != 2 && capability != 4)) return LPB_RESULT_INVALID_ARGUMENT;
    *out = {}; out->struct_size = sizeof(*out); out->api_version = 2; out->flags = 1;
    try {
        windows_readonly_source source;
        const fs::path filename{std::u8string(reinterpret_cast<const char8_t*>(path))};
        if (!source.open(filename)) { out->flags |= 16; return LPB_RESULT_OK; }
        out->file_size = source.size();
        bool incomplete = false, io = false, cancelled = false;
        auto read = [&](uint64_t at, std::span<uint8_t> bytes) {
            if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) { cancelled = true; return false; }
            if (at > source.size() || bytes.size() > source.size() - at ||
                bytes.size() > 4ULL * 1024 * 1024 - out->bytes_read) { incomplete = true; return false; }
            ++out->reserved;
            if (!source.read_at(at, bytes)) { io = true; return false; }
            out->bytes_read += bytes.size(); return true;
        };
        auto block = [&](uint64_t at, uint64_t length, std::vector<uint8_t>& bytes) {
            if (length > 4ULL * 1024 * 1024 - out->bytes_read) { incomplete = true; return false; }
            bytes.resize(static_cast<size_t>(length)); return read(at, bytes);
        };
        std::array<uint8_t, 32> header{};
        if (!read(0, std::span<uint8_t>(header.data(), static_cast<size_t>(std::min<uint64_t>(32,source.size()))))) {
            out->flags |= io ? 16 : 8; return cancelled ? LPB_RESULT_CANCELLED : LPB_RESULT_OK;
        }
        out->image_container = detect_image_container(header);
        out->video_container = detect_video_container(header);
        out->role = out->image_container != LPB_IMAGE_CONTAINER_UNKNOWN ? 1 : 2;
        std::string identifier;
        uint32_t protocol = 0;
        bool disabled_motion = false;
        auto marker = [&](uint32_t p) { if (!(out->flags & 4)) { out->flags |= 4; out->protocol = p; } };
        auto xmp = [&](const std::string& packet) {
            // Parsed XML namespaces and resource semantics, never byte-name matches.
            std::vector<xmp_node> nodes;
            if (!scan_xmp_tree(packet, nodes, true)) { incomplete = true; return; }
            std::string value;
            disabled_motion |= get_attribute_value_in_nodes(nodes,google_camera_namespace,"MotionPhoto")=="0";
            auto positive = [&](std::string_view uri, std::string_view key) {
                std::string text;
                return (text = get_attribute_value_in_nodes(nodes, uri, key)) == "1";
            };
            if (has_attribute_name_in_nodes(nodes,vivo_camera_namespace,"VMotionPhotoVersion") && get_attribute_value_in_nodes(nodes,vivo_camera_namespace,"VMotionPhotoVersion") != "0") marker(LPB_SOURCE_PROTOCOL_VIVO_X300);
            bool motion = positive(google_camera_namespace,"MotionPhoto");
            for (const auto& node : nodes) {
                bool in_directory=false;size_t parent=node.parent_index;
                for(size_t depth=0;parent!=k_no_parent && depth<64;++depth) {
                    if(parent>=nodes.size()){incomplete=true;break;}
                    const auto& owner=nodes[parent];
                    if(node_is(owner,google_container_namespace,"Directory")){in_directory=true;break;}
                    parent=owner.parent_index;
                }
                if(!in_directory)continue;
                std::string_view semantic, mime;
                if (get_node_property_value_checked(nodes,node,"http://ns.google.com/photos/1.0/container/item/","Semantic",semantic) == node_property_result::present &&
                    get_node_property_value_checked(nodes,node,"http://ns.google.com/photos/1.0/container/item/","Mime",mime) == node_property_result::present &&
                    semantic == "MotionPhoto" && mime == "video/mp4") motion = true;
            }
            if (motion) marker(positive(oppo_camera_namespace,"OLivePhotoVersion") ||
                has_attribute_name_in_nodes(nodes,oppo_camera_namespace,"MotionPhotoOwner") ? LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO : LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2);
            if (positive(google_camera_namespace,"MicroVideo")) marker(LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1);
        };
        #include "media/discovery_heif.h"
        if (out->image_container == LPB_IMAGE_CONTAINER_JPEG && (capability == 1 || capability == 4)) {
            uint64_t at = 2; size_t steps = 0; bool stopped = false;
            std::vector<uint8_t> exif_jpeg{0xff,0xd8};
            std::vector<std::string> packets;
            while (at < source.size() && ++steps <= 16384) {
                std::array<uint8_t,4> segment{};
                if (!read(at,segment) || segment[0] != 0xff) { incomplete = true; break; }
                if (segment[1] == 0xda || segment[1] == 0xd9) { stopped = true; break; }
                if (segment[1] == 0xff) { ++at; continue; }
                if (segment[1] == 1) { at += 2; continue; }
                uint64_t length = (uint64_t(segment[2]) << 8) | segment[3];
                if (length < 2 || length > source.size()-at-2) { incomplete = true; break; }
                if (segment[1] == 0xe1) {
                    std::vector<uint8_t> payload;
                    if (!block(at+4,length-2,payload)) break;
                    if (capability == 4 || (payload.size() >= 6 && !std::memcmp(payload.data(),"Exif\0\0",6))) {
                        exif_jpeg.insert(exif_jpeg.end(),segment.begin(),segment.end());
                        exif_jpeg.insert(exif_jpeg.end(),payload.begin(),payload.end());
                    }
                    if (capability == 4 && payload.size() > 29 && !std::memcmp(payload.data(),"http://ns.adobe.com/xap/1.0/\0",29))
                        packets.emplace_back(reinterpret_cast<const char*>(payload.data()+29),payload.size()-29);
                }
                at += length + 2;
            }
            if (!stopped) incomplete = true;
            if (capability == 1) {
                exif_jpeg.insert(exif_jpeg.end(),{0xff,0xda}); bool conflict = false;
                extract_apple_cid_from_image(context,exif_jpeg,LPB_IMAGE_CONTAINER_JPEG,identifier,conflict);
                if (conflict) { identifier.clear(); incomplete = true; }
                protocol = LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;
            } else {
                for (const auto& packet : packets) xmp(packet);
                exif_jpeg.insert(exif_jpeg.end(),{0xff,0xda});
                std::vector<std::string> assembled;
                if (jpeg_xmp_structure_invalid(exif_jpeg,&assembled)) incomplete = true;
                else for (const auto& packet : assembled) xmp(packet);
            }
        }
        if (out->image_container == LPB_IMAGE_CONTAINER_JPEG && capability == 2) {
            // Read only the signed EOF record header, then the exact JSON range.
            const size_t footer_size=static_cast<size_t>(std::min<uint64_t>(source.size(),4096));
            std::vector<uint8_t> footer;
            if(block(source.size()-footer_size,footer_size,footer)) {
                const std::string_view view(reinterpret_cast<const char*>(footer.data()),footer.size());
                const auto album=view.rfind("cameralbum!");
                if(album!=std::string_view::npos && album>=4 && album+15<=view.size()) {
                    const uint64_t record=album+11;
                    const uint64_t record_length=read_be32u(footer.data()+record);
                    const uint64_t json_length=read_be32u(footer.data()+album-4);
                    const uint64_t absolute_album=source.size()-footer_size+album;
                    if(absolute_album<8 || record_length!=footer.size()-record || json_length>absolute_album-8) incomplete=true;
                    else {
                        const uint64_t at=absolute_album-8-json_length;
                        std::vector<uint8_t> tail;
                        if(block(at,source.size()-at,tail)) {
                            size_t start=0;
                            if(!extract_vivo_id_from_image(tail,0,identifier,&start))incomplete=true;
                        }
                    }
                }
            }
            protocol = LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL;
        }
        // Box headers are walked recursively; media/sample payloads are skipped.
        if (out->image_container != LPB_IMAGE_CONTAINER_JPEG) {
            struct box { uint64_t at,length,header; std::array<char,4> type; };
            auto box_at = [&](uint64_t at,uint64_t end,box& b) {
                std::array<uint8_t,16> h{};
                if (end-at < 8 || !read(at,std::span<uint8_t>(h.data(),8))) return false;
                b={at,read_be32u(h.data()),8,{}}; std::memcpy(b.type.data(),h.data()+4,4);
                if (b.length == 1) {
                    if (end-at < 16 || !read(at+8,std::span<uint8_t>(h.data()+8,8))) return false;
                    b.header=16;b.length=0;for(size_t n=8;n<16;++n)b.length=(b.length<<8)|h[n];
                } else if (!b.length) b.length=end-at;
                return b.length >= b.header && b.length <= end-at;
            };
            size_t boxes=0, metas=0;
            auto walk = [&](auto&& self,uint64_t at,uint64_t end,size_t depth) -> bool {
                if (depth > 12) return false;
                while (at < end) {
                    box b{}; if (++boxes > 32768 || !box_at(at,end,b)) return false;
                    const std::string_view type(b.type.data(),4);
                    if (capability == 4 && type == "mpvd") marker(LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC);
                    if (capability == 4 && type == "sefd") {
                        lpb::random_access_reader reader{source.size(),&read,[](void* user,uint64_t offset,std::span<uint8_t> bytes) noexcept {
                            return (*static_cast<decltype(read)*>(user))(offset,bytes);
                        }};
                        lpb::protocols::sef_inventory inventory;std::string error;
                        if(lpb::protocols::observe_sef(reader,at+b.header,at+b.length,inventory,error)) {
                            if(const auto* tag=inventory.find(0x0a30);tag&&tag->name=="MotionPhoto_Data")marker(LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC);
                        } else incomplete=true;
                    }
                    if (capability == 1 && type == "meta" && out->image_container == LPB_IMAGE_CONTAINER_UNKNOWN) {
                        std::vector<uint8_t> owned;
                        if (!block(at,b.length,owned)) return false;
                        std::vector<uint8_t> moov(8);uint32_t n=static_cast<uint32_t>(owned.size()+8);
                        moov[0]=uint8_t(n>>24);moov[1]=uint8_t(n>>16);moov[2]=uint8_t(n>>8);moov[3]=uint8_t(n);
                        std::memcpy(moov.data()+4,"moov",4);moov.insert(moov.end(),owned.begin(),owned.end());
                        std::string id;extract_apple_cid_from_video(context,moov,id);
                        if (!id.empty()) { if (!identifier.empty()) return false; identifier=std::move(id); }
                        ++metas;
                    } else if (capability == 2 && type == "uuid") {
                        std::array<uint8_t,16> owner{};
                        if (b.length-b.header < 16 || !read(at+b.header,owner)) return false;
                        if (!std::memcmp(owner.data(),"vivoMediaExtInfo",16)) {
                            std::vector<uint8_t> owned;if (!block(at,b.length,owned)) return false;
                            std::string id;extract_vivo_id_from_video(owned,id);
                            if (!id.empty()) { if (!identifier.empty()) return false;identifier=std::move(id); }
                        }
                    } else if (type == "moov" || type == "udta") {
                        if (!self(self,at+b.header,at+b.length,depth+1)) return false;
                    } else if (type == "meta" && out->image_container == LPB_IMAGE_CONTAINER_HEIC) {
                        if (capability != 2 && !heif_metadata(at,b.length,b.header)) incomplete = true;
                    }
                    at += b.length;
                }
                return true;
            };
            if (!walk(walk,0,source.size(),0)) incomplete = true;
            protocol = capability == 1 ? LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO : LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL;
        }
        if (capability == 4 && out->image_container == LPB_IMAGE_CONTAINER_JPEG && source.size() >= 8) {
            std::array<uint8_t,8> end{};
            if (read(source.size()-8,end) && !std::memcmp(end.data()+4,"SEFT",4)) {
                lpb::random_access_reader reader{source.size(),&read,[](void* user,uint64_t at,std::span<uint8_t> bytes) noexcept {
                    return (*static_cast<decltype(read)*>(user))(at,bytes);
                }};
                lpb::protocols::sef_inventory inventory;std::string error;
                if (lpb::protocols::observe_sef(reader,0,source.size(),inventory,error)) {
                    if (const auto* tag=inventory.find(0x0a30); tag && tag->name=="MotionPhoto_Data") {
                        out->flags&=~4u;marker(LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG);
                    }
                } else incomplete=true;
            }
        }
        if (capability == 4 && out->image_container != LPB_IMAGE_CONTAINER_UNKNOWN && source.size() >= 60) {
            std::array<uint8_t,60> tail{};
            if (read(source.size()-60,tail)) {
                // The LIVE field is owned by the fixed last 20-byte record.
                std::string_view field(reinterpret_cast<const char*>(tail.data()+40),20);
                if (field.starts_with("LIVE_") && field.size()>5 && field[5]>='0' && field[5]<='9') marker(LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO);
            }
        }
        if (cancelled || lpb_context_check_cancelled(context) != LPB_RESULT_OK) return LPB_RESULT_CANCELLED;
        if (!source.capture_identity(out->identity) || !source.verify_same_identity(filename)) return LPB_RESULT_SOURCE_CHANGED;
        if (disabled_motion && (out->flags & 4) && out->protocol != LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1) incomplete = true;
        if (incomplete) out->flags |= 8;
        if (io) out->flags |= 16;
        if (!identifier.empty() && identifier.size()<sizeof(out->identifier) && !incomplete && !io) {
            out->flags |= 2;out->protocol=protocol;std::memcpy(out->identifier,identifier.data(),identifier.size());
        }
        return LPB_RESULT_OK;
    } catch (const std::bad_alloc&) { return LPB_RESULT_UNSUPPORTED; }
      catch (...) { return LPB_RESULT_INTERNAL_ERROR; }
}
