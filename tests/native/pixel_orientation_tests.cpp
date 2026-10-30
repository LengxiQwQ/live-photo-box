#include "media/pixel_orientation.h"
#include <array>
#include <iostream>
#include <stdexcept>
#include <string>

using lpb::media::pixel_surface;
void require(bool yes,const char* message){if(!yes)throw std::runtime_error(message);}
uint16_t value(unsigned label,unsigned channel,unsigned bits){return static_cast<uint16_t>((bits==8?0U:32768U)+label*17U+channel);}
pixel_surface fixture(unsigned channels,unsigned bits,unsigned padding=0) {
 pixel_surface s;s.width=3;s.height=2;s.channels=channels;s.storage_bit_depth=bits;s.signal_bit_depth=bits;
 s.stride=uint64_t(3)*channels*(bits/8)+padding;s.pixels.assign(static_cast<size_t>(s.stride*s.height),0xa5);
 s.color.has_icc=true;s.color.icc_profile={9,8,7,6};s.color.has_nclx=true;s.color.primaries=9;s.color.transfer=16;s.color.matrix=9;s.color.full_range=true;
 for(unsigned y=0;y<2;++y)for(unsigned x=0;x<3;++x)for(unsigned c=0;c<channels;++c) {
  const auto n=value(y*3+x+1,c,bits);const auto at=static_cast<size_t>(y*s.stride+(x*channels+c)*(bits/8));
  s.pixels[at]=static_cast<uint8_t>(n);if(bits==16)s.pixels[at+1]=static_cast<uint8_t>(n>>8);
 }
 return s;
}
void unchanged(const pixel_surface& a,const pixel_surface& b) {
 require(a.width==b.width&&a.height==b.height&&a.channels==b.channels&&a.storage_bit_depth==b.storage_bit_depth&&
  a.signal_bit_depth==b.signal_bit_depth&&a.stride==b.stride&&a.pixels==b.pixels,"failure/identity preserves entire source surface");
 require(a.color.has_icc==b.color.has_icc&&a.color.has_nclx==b.color.has_nclx&&a.color.icc_profile==b.color.icc_profile&&
  a.color.primaries==b.color.primaries&&a.color.transfer==b.color.transfer&&a.color.matrix==b.color.matrix&&a.color.full_range==b.color.full_range,"color descriptor unchanged");
}
int main() {try {
 // Independent hand-labelled 3x2 patterns, including both transpose mirrors.
 constexpr std::array<std::array<unsigned,6>,8> expected{{
  {{1,2,3,4,5,6}},{{3,2,1,6,5,4}},{{6,5,4,3,2,1}},{{4,5,6,1,2,3}},
  {{1,4,2,5,3,6}},{{4,1,5,2,6,3}},{{6,3,5,2,4,1}},{{3,6,2,5,1,4}}
 }};
 unsigned cases{};
 for(unsigned channels:{3U,4U})for(unsigned bits:{8U,16U})for(unsigned padding:{0U,5U})for(unsigned orientation=1;orientation<=8;++orientation) {
  auto s=fixture(channels,bits,padding),original=s;require(lpb::media::normalize_pixel_orientation(s,orientation),"known physical orientation succeeds");
  require(s.width==(orientation>=5?2U:3U)&&s.height==(orientation>=5?3U:2U),"actual output geometry swaps only for transposed orientations");
  for(unsigned y=0;y<s.height;++y)for(unsigned x=0;x<s.width;++x)for(unsigned c=0;c<channels;++c) {
   const auto at=static_cast<size_t>(y*s.stride+(x*channels+c)*(bits/8));
   const unsigned observed=s.pixels[at]+(bits==16?(unsigned(s.pixels[at+1])<<8):0U);
   require(observed==value(expected[orientation-1][y*s.width+x],c,bits),"entire RGB/RGBA high-depth value physically mapped to hand-labelled destination");
  }
  const auto old_pixels=original.pixels;original.width=s.width;original.height=s.height;original.stride=s.stride;original.pixels=s.pixels;
  unchanged(original,s);if(orientation==1)require(s.pixels==old_pixels,"normal orientation keeps padding bytes too");++cases;
 }
 for(unsigned orientation=1;orientation<=8;++orientation) {
  auto s=fixture(4,16),original=s;require(lpb::media::normalize_pixel_orientation(s,orientation),"inverse fixture forward");
  const unsigned inverse=orientation==6?8:orientation==8?6:orientation;
  require(lpb::media::normalize_pixel_orientation(s,inverse),"inverse fixture backward");unchanged(original,s);
 }
 for(unsigned defect=0;defect<12;++defect) {
  auto s=fixture(3,8);unsigned orientation=6;
  switch(defect) {
  case 0:orientation=0;break;case 1:orientation=9;break;case 2:s.width=0;break;case 3:s.height=0;break;
  case 4:s.channels=2;break;case 5:s.storage_bit_depth=24;break;case 6:s.signal_bit_depth=9;break;
  case 7:s.signal_bit_depth=7;break;case 8:s.stride=1;break;case 9:s.pixels.pop_back();break;
  case 10:s.stride=UINT64_MAX;break;case 11:s.width=UINT32_MAX;s.height=UINT32_MAX;break;
  }
  const auto original=s;require(!lpb::media::normalize_pixel_orientation(s,orientation),"invalid/unknown/overflow surface fails without partial mutation");unchanged(original,s);
 }
 std::cout<<cases<<" hand-labelled all-orientation/8-16bit/RGB-RGBA/padded cases;8inverse and12failure-atomic checks passed\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
