#pragma once
#include "media/heic_backend.h"
#include <cstring>
#include <limits>

namespace lpb::media {
// Physical stored-pixel transform, independent of protocol and metadata tags.
// The caller must resolve a unique effective orientation, apply the appropriate
// relation to every owned image, and separately normalize/verify container and
// Exif metadata. This function alone is not a preservation or publish proof.
inline bool normalize_pixel_orientation(pixel_surface& image,uint32_t orientation) noexcept {
    constexpr uint64_t max_pixels=100ULL*1024*1024;
    if(orientation<1U||orientation>8U||!image.width||!image.height||
        uint64_t(image.width)*image.height>max_pixels||
        (image.channels!=3U&&image.channels!=4U)||
        (image.storage_bit_depth!=8U&&image.storage_bit_depth!=16U)||
        image.signal_bit_depth<8U||image.signal_bit_depth>image.storage_bit_depth)return false;
    const uint64_t pixel_bytes=uint64_t(image.channels)*(image.storage_bit_depth/8U);
    const uint64_t row_bytes=uint64_t(image.width)*pixel_bytes;
    if(image.stride<row_bytes||image.stride>std::numeric_limits<uint64_t>::max()/image.height||
        image.stride*image.height!=image.pixels.size())return false;
    if(orientation==1U)return true;
    const uint32_t width=orientation>=5U?image.height:image.width;
    const uint32_t height=orientation>=5U?image.width:image.height;
    const uint64_t stride=uint64_t(width)*pixel_bytes,total=stride*height;
    if(total>std::numeric_limits<size_t>::max())return false;
    try {
        std::vector<uint8_t> result(static_cast<size_t>(total));
        for(uint32_t y=0;y<image.height;++y)for(uint32_t x=0;x<image.width;++x) {
            uint32_t dx{},dy{};
            switch(orientation) {
            case 2:dx=image.width-1U-x;dy=y;break;
            case 3:dx=image.width-1U-x;dy=image.height-1U-y;break;
            case 4:dx=x;dy=image.height-1U-y;break;
            case 5:dx=y;dy=x;break;
            case 6:dx=image.height-1U-y;dy=x;break;
            case 7:dx=image.height-1U-y;dy=image.width-1U-x;break;
            case 8:dx=y;dy=image.width-1U-x;break;
            default:return false;
            }
            std::memcpy(result.data()+static_cast<size_t>(uint64_t(dy)*stride+uint64_t(dx)*pixel_bytes),
                image.pixels.data()+static_cast<size_t>(uint64_t(y)*image.stride+uint64_t(x)*pixel_bytes),
                static_cast<size_t>(pixel_bytes));
        }
        image.pixels=std::move(result);image.width=width;image.height=height;image.stride=stride;
        return true;
    }catch(...){return false;}
}
}
