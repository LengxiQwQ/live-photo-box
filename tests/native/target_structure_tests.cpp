#include "protocols/target_structure.h"
#include <stdexcept>
#include <iostream>
int main() {
    using namespace lpb::target;
    if(!profile_accepts(1,0)||!profile_accepts(1,1)||profile_accepts(1,2)||
       !profile_accepts(2,3)||profile_accepts(2,2)||!profile_accepts(3,0)||
       profile_accepts(3,1)||!profile_accepts(4,0)||!profile_accepts(5,2)||
       profile_accepts(5,3)||!profile_accepts(6,4)||profile_accepts(7,0)||
       profile_accepts(8,0)||profile_accepts(9,0)||profile_accepts(0,0))
        throw std::runtime_error("Closed OutputProfile matrix or deferred family boundary changed");
    if(!contained_range(UINT64_MAX,UINT64_MAX,0)||contained_range(UINT64_MAX,UINT64_MAX,1)||
       contained_range(64,UINT64_MAX,2)||!contained_range(64,32,32)||contained_range(64,32,33))
        throw std::runtime_error("64-bit range boundary overflow");
    std::cout<<"P9R1_TargetStructure: closed profiles and 64-bit range boundaries PASS\n";
}
