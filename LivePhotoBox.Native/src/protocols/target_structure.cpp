#include "protocols/target_structure.h"
namespace lpb::target {
bool contained_range(uint64_t size,uint64_t offset,uint64_t length) noexcept {
    return offset<=size && length<=size-offset;
}
bool profile_accepts(uint32_t target,uint32_t format) noexcept {
    switch(target) {
    case 1:return format==0||format==1;
    case 2:return format==0||format==1||format==3;
    case 3:case 4:return format==0;
    case 5:return format==0||format==2;
    case 6:return format==0||format==2||format==4;
    default:return false;
    }
}
}
