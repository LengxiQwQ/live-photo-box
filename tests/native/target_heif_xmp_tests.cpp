#include "containers/heif_internal.h"
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

// The static object also contains legacy context-reporting C entry points.
// This test exercises only context-free graph helpers; reaching a context
// callback is a test failure, never a substituted product success verdict.
void set_error(lpb_context*,const char*) noexcept { std::terminate(); }

#define REQUIRE(x) do { if (!(x)) throw std::runtime_error(std::string("line ") + std::to_string(__LINE__) + ": " + #x); } while (false)
namespace {
using bytes = std::vector<uint8_t>;
void number(bytes& b, uint64_t n, size_t width) { for (size_t i = width; i; --i) b.push_back(static_cast<uint8_t>(n >> ((i-1)*8))); }
uint64_t integer(const bytes& b, size_t& p, size_t width) {
    REQUIRE(width <= 8 && p <= b.size() && width <= b.size()-p);
    uint64_t n = 0; while (width--) n = (n << 8) | b[p++]; return n;
}
void concat(bytes& b, const bytes& child) { b.insert(b.end(), child.begin(), child.end()); }
bytes box(const char* type, bytes payload) {
    bytes result; number(result, payload.size()+8, 4);
    result.insert(result.end(), type, type+4); concat(result, payload); return result;
}
void patch32(bytes& b, size_t p, uint32_t value) { for (size_t i = 0; i < 4; ++i) b[p+3-i] = static_cast<uint8_t>(value >> (i*8)); }
struct node { size_t start{}, body{}, end{}; std::string type; };
// Independent, deliberately small observer for this test's fixtures. It uses no
// production box parser, locator, item enumeration, XMP selector or byte reader.
std::vector<node> boxes(const bytes& b, size_t p, size_t end) {
    std::vector<node> result;
    while (p < end) {
        REQUIRE(end-p >= 8); const size_t start = p;
        const uint64_t n = integer(b, p, 4); REQUIRE(n >= 8 && n <= end-start);
        std::string type(b.begin()+static_cast<ptrdiff_t>(p), b.begin()+static_cast<ptrdiff_t>(p+4)); p += 4;
        result.push_back({start,p,start+static_cast<size_t>(n),type}); p=start+static_cast<size_t>(n);
    }
    REQUIRE(p == end); return result;
}
node unique(const std::vector<node>& nodes, const char* type) {
    std::vector<node> selected; for (const auto& n : nodes) if (n.type == type) selected.push_back(n);
    REQUIRE(selected.size() == 1); return selected.front();
}
struct observed {
    uint32_t primary{}, xmp{};
    std::map<uint32_t, bytes> payloads;
    std::map<std::string, bytes> meta_children;
    std::map<uint32_t, uint16_t> methods;
    std::map<uint32_t, std::vector<uint64_t>> offsets;
    std::map<uint32_t, std::vector<uint64_t>> indexes;
};
observed observe(const bytes& b) {
    observed o;
    const auto top = boxes(b, 0, b.size());
    const auto meta = unique(top, "meta");
    const auto children = boxes(b, meta.body+4, meta.end);
    for (const auto& n : children) o.meta_children[n.type] = bytes(b.begin()+static_cast<ptrdiff_t>(n.start), b.begin()+static_cast<ptrdiff_t>(n.end));
    const auto pitm = unique(children, "pitm"); size_t p = pitm.body;
    const auto pv = integer(b,p,4); o.primary = static_cast<uint32_t>(integer(b,p,(pv>>24) ? 4 : 2));
    size_t idat_body = 0; for (const auto& child : children) if (child.type == "idat") idat_body=child.body;
    const auto iloc = unique(children, "iloc"); p=iloc.body;
    const auto version=integer(b,p,4)>>24, widths=integer(b,p,1), widths2=integer(b,p,1);
    const auto count=integer(b,p,version<2 ? 2 : 4);
    for (uint64_t i=0; i<count; ++i) {
        const auto id=static_cast<uint32_t>(integer(b,p,version<2 ? 2 : 4));
        const auto method=static_cast<uint16_t>(version ? integer(b,p,2) : 0);
        REQUIRE(integer(b,p,2) == 0); const auto base=integer(b,p,widths2>>4);
        const auto extent_count=integer(b,p,2); o.methods[id]=method;
        for (uint64_t e=0; e<extent_count; ++e) {
            const auto index=integer(b,p,version ? widths2&15 : 0);
            const auto off=integer(b,p,widths>>4), length=integer(b,p,widths&15);
            const uint64_t absolute=base+off+(method==1 ? idat_body : 0);
            REQUIRE(absolute <= b.size() && length <= b.size()-absolute);
            auto& data=o.payloads[id]; data.insert(data.end(),b.begin()+static_cast<ptrdiff_t>(absolute),b.begin()+static_cast<ptrdiff_t>(absolute+length));
            o.offsets[id].push_back(off); o.indexes[id].push_back(index);
        }
    }
    REQUIRE(p == iloc.end);
    for (const auto& child : children) if (child.type=="iref") {
        p=child.body; const auto wide=integer(b,p,4)>>24;
        for (const auto& ref : boxes(b,p,child.end)) if (ref.type=="cdsc") {
            p=ref.body; const auto source=integer(b,p,wide ? 4 : 2); const auto count2=integer(b,p,2);
            if (count2==1 && integer(b,p,wide ? 4 : 2)==o.primary) { REQUIRE(!o.xmp); o.xmp=static_cast<uint32_t>(source); }
        }
    }
    return o;
}

bytes infe(uint16_t id, const char* type, const std::string& mime={}) {
    bytes p{2,0,0,0}; number(p,id,2); number(p,0,2); p.insert(p.end(),type,type+4); p.push_back(0);
    if (std::string(type)=="mime") { p.insert(p.end(),mime.begin(),mime.end()); p.push_back(0); p.push_back(0); }
    return box("infe",p);
}
const bytes primary{11,12,13,14}, ordinary{71,72,73,74,75};
const std::string old_xml="<old ordinary='preserve'/>";
const std::string new_xml="<new ordinary='preserve' motion='1'/>";
struct options {
    bool existing{}, method1{}, primary_in_idat{}, split_xmp{}, mdat_first{}, duplicate_owner{}, shared_owner{}, crossing{}, overlap{}, external{}, bad_width{}, size0{};
};
bytes fixture(options options={}) {
    bytes infos{0,0,0,0}; number(infos,options.existing ? 3 : 2,2);
    concat(infos,infe(1,"hvc1")); concat(infos,infe(2,"mime","application/opaque"));
    if (options.existing) concat(infos,infe(3,"mime","application/rdf+xml"));
    bytes ipayload; if(options.primary_in_idat) concat(ipayload,primary);
    const size_t ordinary_relative=ipayload.size(); concat(ipayload,ordinary);
    const size_t xml_relative=ipayload.size(); if(options.existing && options.method1) ipayload.insert(ipayload.end(),old_xml.begin(),old_xml.end());
    const bytes idat=box("idat",ipayload), unknown=box("zzzz",bytes{3,1,4,1,5,9});
    bytes mdat_payload=primary;
    concat(mdat_payload,ordinary); if(options.existing && !options.method1) mdat_payload.insert(mdat_payload.end(),old_xml.begin(),old_xml.end());
    const auto make_meta=[&](size_t mdat_body, size_t meta_start) {
        bytes loc{1,0,0,0,0x44,0x44}; number(loc,options.existing ? 3 : 2,2);
        // The idat is deliberately before the changing iloc, while iinf changes
        // before idat: absolute method0/idat and relative method1 are distinct.
        const size_t idat_body=meta_start+12+box("iinf",infos).size()+8;
        const auto entry=[&](uint16_t id, bool relative, uint64_t offset, uint64_t length, bool split=false) {
            number(loc,id,2); number(loc,relative ? 1 : 0,2); number(loc,options.external && id==2 ? 1 : 0,2);
            number(loc,0,4); number(loc,split ? 2 : 1,2);
            number(loc,0,4); number(loc,offset,4); number(loc,split ? length/2 : length,4);
            if(split) { number(loc,1,4); number(loc,offset+length/2,4); number(loc,length-length/2,4); }
        };
        entry(1,false,options.primary_in_idat ? idat_body : mdat_body,options.crossing ? mdat_payload.size()+1 : primary.size());
        entry(2,options.method1,options.overlap ? mdat_body : options.method1 ? ordinary_relative : mdat_body+primary.size(),ordinary.size());
        if(options.existing) entry(3,options.method1,options.method1 ? xml_relative : mdat_body+primary.size()+ordinary.size(),old_xml.size(),options.split_xmp);
        if(options.bad_width) loc[4]=0x94;
        bytes p(4,0); concat(p,box("iinf",infos)); concat(p,idat); concat(p,unknown); concat(p,box("iloc",loc));
        if(options.existing) {
            bytes ref{0,0,0,0}, cdsc; number(cdsc,3,2); number(cdsc,options.shared_owner ? 2 : 1,2); number(cdsc,1,2);
            if(options.shared_owner) number(cdsc,2,2);
            concat(ref,box("cdsc",cdsc)); if(options.duplicate_owner) concat(ref,box("cdsc",cdsc)); concat(p,box("iref",ref));
        }
        bytes pitm{0,0,0,0,0,1}; concat(p,box("pitm",pitm)); return box("meta",p);
    };
    const bytes ftyp=box("ftyp",bytes{'h','e','i','c',0,0,0,0,'m','i','f','1'}), mdat=box("mdat",mdat_payload);
    const size_t meta_start=ftyp.size()+(options.mdat_first ? mdat.size() : 0);
    auto meta=make_meta(0,meta_start);
    const size_t mdat_body=options.mdat_first ? ftyp.size()+8 : ftyp.size()+meta.size()+8;
    meta=make_meta(mdat_body,meta_start);
    bytes output=ftyp;
    if(options.mdat_first) concat(output,mdat); concat(output,meta); if(!options.mdat_first) concat(output,mdat);
    if(options.size0) patch32(output,options.mdat_first ? ftyp.size() : ftyp.size()+meta.size(),0);
    return output;
}
void positive(options options) {
    const auto input=fixture(options); const auto before=observe(input);
    uint32_t primary_id=0; std::string xml,error;
    REQUIRE(lpb::containers::read_primary_xmp(input,primary_id,xml,error)); REQUIRE(primary_id==1);
    REQUIRE(xml==(options.existing ? old_xml : ""));
    bytes output;
    REQUIRE(lpb::containers::rewrite_primary_xmp(input,new_xml,output,error));
    const auto after=observe(output);
    REQUIRE(after.primary==before.primary && after.xmp!=0);
    REQUIRE(after.payloads.at(1)==primary);
    REQUIRE(after.payloads.at(2)==ordinary);
    REQUIRE(after.payloads.at(after.xmp)==bytes(new_xml.begin(),new_xml.end()));
    REQUIRE(after.meta_children.at("zzzz")==before.meta_children.at("zzzz"));
    REQUIRE(after.meta_children.at("pitm")==before.meta_children.at("pitm"));
    REQUIRE(after.methods.at(2)==(options.method1 ? 1 : 0));
    if(options.method1) REQUIRE(after.offsets.at(2)==before.offsets.at(2));
    if(options.existing) {
        REQUIRE(after.xmp==before.xmp);
        REQUIRE(after.meta_children.at("iinf")==before.meta_children.at("iinf"));
        REQUIRE(after.meta_children.at("iref")==before.meta_children.at("iref"));
    }
    // No unreferenced old packet remains in any top-level/embedded payload.
    REQUIRE(std::search(output.begin(),output.end(),old_xml.begin(),old_xml.end())==output.end() || !options.existing);
    REQUIRE(lpb::containers::read_primary_xmp(output,primary_id,xml,error)); REQUIRE(xml==new_xml);
}
void negative(options options, const char* reason) {
    const auto input=fixture(options); bytes output{1,2}; std::string error;
    REQUIRE(!lpb::containers::rewrite_primary_xmp(input,new_xml,output,error));
    REQUIRE(output.empty() && error.find(reason)!=std::string::npos);
}
}
int main() {
    try {
        positive({});
        options existing; existing.existing=true; positive(existing);
        existing.split_xmp=true; positive(existing);
        existing.method1=true; positive(existing);
        existing.primary_in_idat=true; positive(existing);
        existing.mdat_first=true; positive(existing);
        options absolute_idat; absolute_idat.primary_in_idat=true; positive(absolute_idat);
        options first; first.mdat_first=true; positive(first);
        options bad; bad.existing=true; bad.duplicate_owner=true; negative(bad,"duplicate/shadow");
        bad={}; bad.existing=true; bad.shared_owner=true; negative(bad,"unique cdsc");
        bad={}; bad.crossing=true; negative(bad,"out-of-owner");
        bad={}; bad.overlap=true; negative(bad,"overlapping");
        bad={}; bad.external=true; negative(bad,"external reference");
        bad={}; bad.bad_width=true; negative(bad,"field width");
        bad={}; bad.size0=true; negative(bad,"size-zero top-level");
        std::cout << "HEIF XMP item graph transport: 8 positive / 7 rejection cases passed\n";
        return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
