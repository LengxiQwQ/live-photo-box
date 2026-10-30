#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace lpb::media {
// Read-only, bounded AudioSpecificConfig validation for unmodified AAC sample
// payloads. Core channel configuration and the SBR/PS output are distinct;
// mono AAC-LC with SBR may carry implicit parametric stereo in coded samples.
// No samples are decoded/re-encoded and no audio-preservation claim is made.
// Field ordering cross-checked against FFmpeg's MPEG-4 audio config parser:
// https://www.ffmpeg.org/doxygen/8.0/mpeg4audio_8c_source.html
inline bool aac_config_matches_entry(std::span<const uint8_t> config,
    uint16_t entry_channels,uint32_t entry_rate_fixed,bool* header_mismatch=nullptr) noexcept {
    if(header_mismatch)*header_mismatch=false;
    const auto header=[&](bool matches){if(header_mismatch)*header_mismatch=!matches;return matches;};
    constexpr std::array<uint32_t,13> rates{96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350};
    constexpr std::array<uint16_t,8> channels{0,1,2,3,4,5,6,8};
    if(config.size()<2||config.size()>16)return false;
    const unsigned object=config[0]>>3;
    if(object==2) {
        const unsigned frequency=((config[0]&7U)<<1)|(config[1]>>7);
        const unsigned channel=(config[1]>>3)&15U;
        if(frequency>=rates.size()||!channel||channel>=channels.size()||(config[1]&7U)||
            !(config.size()==2||(config.size()==5&&config[2]==0x56&&config[3]==0xe5&&!config[4])))return false;
        return header(entry_channels==channels[channel]&&static_cast<uint64_t>(entry_rate_fixed)==(static_cast<uint64_t>(rates[frequency])<<16));
    }
    if(object!=5&&object!=29)return false;
    std::size_t bit=5;
    const auto read=[&](unsigned width,uint32_t& value) {
        if(width>24||bit+width>config.size()*8)return false;
        value=0;
        for(unsigned i=0;i<width;++i,++bit)value=(value<<1)|((config[bit/8]>>(7-bit%8))&1U);
        return true;
    };
    const auto rate=[&](uint32_t& value) {
        uint32_t index=0;if(!read(4,index))return false;
        if(index==15)return read(24,value)&&value>0&&value<=192000;
        if(index>=rates.size())return false;
        value=rates[index];return true;
    };
    uint32_t core_rate=0,channel=0,output_rate=0,core_object=0,ga_flags=0;
    if(!rate(core_rate)||!read(4,channel)||!channel||channel>=channels.size()||
       !rate(output_rate)||!read(5,core_object)||core_object!=2||!read(3,ga_flags)||ga_flags||
       (output_rate!=core_rate&&output_rate!=core_rate*2))return false;
    // PCEs, 960-frame syntax, core-coder dependency, extensionFlag and further
    // extensions need their own structural model. Only byte-alignment zeroes
    // may follow this complete explicit LC+SBR/PS configuration.
    if(config.size()*8-bit>7)return false;
    uint32_t padding=0;if(!read(static_cast<unsigned>(config.size()*8-bit),padding)||padding)return false;
    if(object==29&&channel!=1)return false;
    const bool channel_matches=object==29?entry_channels==2:
        entry_channels==channels[channel]||(channel==1&&entry_channels==2);
    return header(channel_matches&&static_cast<uint64_t>(entry_rate_fixed)==(static_cast<uint64_t>(output_rate)<<16));
}
}
