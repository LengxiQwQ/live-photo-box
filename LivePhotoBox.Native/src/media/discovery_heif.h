// Bounded HEIF metadata locator, included in the v2 discovery function.
// Only the primary's cdsc metadata is read. Item payloads stay at their original
// offsets; mdat/idat media bytes are never materialized as a synthetic file.
auto heif_metadata = [&](uint64_t meta_at,uint64_t meta_length,uint64_t meta_header) {
    std::vector<uint8_t> data;
    if (!block(meta_at,meta_length,data)) return false;
    struct item { uint32_t type{}; bool xml{}; };
    struct extent { uint64_t offset{},length{}; };
    std::map<uint32_t,item> items;
    std::map<uint32_t,std::vector<extent>> locations;
    std::map<uint32_t,std::vector<uint32_t>> owners;
    uint32_t primary=0;uint64_t idat=0,idat_size=0;bool have_iloc=false,have_iinf=false;
    auto number = [&](size_t& p,size_t end,unsigned n,uint64_t& value) {
        if(n>8 || p>end || n>end-p)return false;
        value=0;while(n--)value=(value<<8)|data[p++];return true;
    };
    auto boxes = [&](size_t begin,size_t end,auto&& visit)->bool {
        size_t count=0;
        for(size_t p=begin;p<end;) {
            if(++count>16384 || end-p<8)return false;
            uint64_t size=read_be32u(data.data()+p);size_t h=8;
            if(size==1){if(end-p<16)return false;size=0;for(size_t j=8;j<16;++j)size=(size<<8)|data[p+j];h=16;}
            else if(!size)size=end-p;
            if(size<h||size>end-p)return false;
            if(!visit(p,p+static_cast<size_t>(size),h))return false;
            p+=static_cast<size_t>(size);
        }
        return true;
    };
    bool ok=boxes(static_cast<size_t>(meta_header)+4,data.size(),[&](size_t p,size_t end,size_t h) {
        std::string_view type(reinterpret_cast<const char*>(data.data()+p+4),4);
        size_t q=p+h;uint64_t v=0;
        if(type=="pitm") {
            if(end-q<4 || primary)return false;unsigned version=data[q];q+=4;
            if(version>1||!number(q,end,version?4:2,v)||!v)return false;primary=static_cast<uint32_t>(v);
        } else if(type=="idat") { if(idat)return false;idat=meta_at+q;idat_size=end-q;
        } else if(type=="iinf") {
            if(have_iinf||end-q<4)return false;have_iinf=true;unsigned version=data[q];q+=4;
            if(version>1||!number(q,end,version?4:2,v))return false;uint64_t count=v,seen=0;
            if(!boxes(q,end,[&](size_t a,size_t b,size_t bh) {
                ++seen;if(std::memcmp(data.data()+a+4,"infe",4))return false;
                size_t c=a+bh;if(b-c<4)return false;unsigned version=data[c];c+=4;
                uint64_t id=0,protection=0,kind=0;
                if((version!=2&&version!=3)||!number(c,b,version==3?4:2,id)||!id||
                   !number(c,b,2,protection)||protection||!number(c,b,4,kind)||items.contains(static_cast<uint32_t>(id)))return false;
                while(c<b&&data[c])++c;if(c==b)return false;++c;
                bool xml=false;
                if(kind==0x6d696d65) { // mime
                    size_t start=c;while(c<b&&data[c])++c;if(c==b)return false;
                    xml=std::string_view(reinterpret_cast<const char*>(data.data()+start),c-start)=="application/rdf+xml";
                }
                items[static_cast<uint32_t>(id)]={static_cast<uint32_t>(kind),xml};return true;
            }))return false;
            if(seen!=count)return false;
        } else if(type=="iref") {
            if(end-q<4)return false;unsigned version=data[q];q+=4;if(version>1)return false;
            if(!boxes(q,end,[&](size_t a,size_t b,size_t bh) {
                if(std::memcmp(data.data()+a+4,"cdsc",4))return true;
                size_t c=a+bh;uint64_t from=0,count=0,to=0;
                if(!number(c,b,version?4:2,from)||!number(c,b,2,count)||count>16384)return false;
                for(uint64_t i=0;i<count;++i) {if(!number(c,b,version?4:2,to))return false;owners[static_cast<uint32_t>(from)].push_back(static_cast<uint32_t>(to));}
                return c==b;
            }))return false;
        } else if(type=="iloc") {
            if(have_iloc||end-q<6)return false;have_iloc=true;unsigned version=data[q];q+=4;
            if(version>2)return false;
            unsigned offset_size=data[q]>>4,length_size=data[q++]&15,base_size=data[q]>>4,index_size=(version?data[q]&15:0);++q;
            uint64_t count=0;if(!number(q,end,version==2?4:2,count)||count>16384)return false;
            for(uint64_t i=0;i<count;++i) {
                uint64_t id=0,method=0,reference=0,base=0,n=0;
                if(!number(q,end,version==2?4:2,id)||(version&&!number(q,end,2,method))||
                    !number(q,end,2,reference)||reference||method>1||!number(q,end,base_size,base)||
                    !number(q,end,2,n)||n>64||locations.contains(static_cast<uint32_t>(id)))return false;
                auto& ranges=locations[static_cast<uint32_t>(id)];
                // Retain construction method temporarily in the high marker.
                for(uint64_t e=0;e<n;++e) {
                    uint64_t index=0,offset=0,length=0;
                    if(!number(q,end,index_size,index)||index||!number(q,end,offset_size,offset)||
                       !number(q,end,length_size,length)||!length||offset>UINT64_MAX-base)return false;
                    offset+=base;if(offset>INT64_MAX)return false;
                    ranges.push_back({offset|(method?1ULL<<63:0),length});
                }
            }
            if(q!=end)return false;
        }
        return true;
    });
    if(!ok||!primary||!have_iinf||!have_iloc)return false;
    size_t found=0;
    for(const auto& [id,item]:items) {
        bool selected=capability==1?item.type==0x45786966:item.xml;
        if(!selected)continue;
        const auto owner=owners.find(id);
        if(owner==owners.end() || owner->second!=std::vector<uint32_t>{primary})continue;
        if(++found>1)return false;
        auto loc=locations.find(id);if(loc==locations.end()||loc->second.empty())return false;
        std::vector<uint8_t> payload;
        for(auto extent:loc->second) {
            if(extent.offset&(1ULL<<63)) {
                extent.offset&=~(1ULL<<63);
                if(!idat||extent.offset>idat_size||extent.length>idat_size-extent.offset)return false;
                extent.offset+=idat;
            }
            std::vector<uint8_t> part;if(!block(extent.offset,extent.length,part))return false;
            payload.insert(payload.end(),part.begin(),part.end());
        }
        if(capability==1) {
            if(payload.size()<4)return false;
            size_t start=0;
            if(payload.size()>=6&&!std::memcmp(payload.data(),"Exif\0\0",6))start=6;
            else if(!((payload[0]=='I'&&payload[1]=='I')||(payload[0]=='M'&&payload[1]=='M'))) {
                uint64_t skip=read_be32u(payload.data());if(skip>payload.size()-4)return false;start=static_cast<size_t>(skip)+4;
            }
            bool conflict=false;extract_apple_cid_from_owned_exif(payload.data(),start,payload.size(),identifier,conflict);
            if(conflict)return false;
        } else xmp(std::string(reinterpret_cast<const char*>(payload.data()),payload.size()));
    }
    return true;
};
