#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

#define DENOISE_WEB_TARGET_CW 1
#define DENOISE_WEB_TARGET_VOICE 0

extern "C" {
#include "denoise_audio.h"
#include "denoise_model.h"
}

#include "cw_model_weights.h"
#include "voice_v2_engine.h"

#if defined(_WIN32)
#define SKYROOF_EXPORT extern "C" __declspec(dllexport)
#else
#define SKYROOF_EXPORT extern "C"
#endif

namespace {
constexpr int kAbiVersion = 1;
constexpr int kV2HopLength = 96;
constexpr int kV2LatencyHops = 3;
constexpr int kV2GainCapacity = 256;

std::mutex g_v2_mutex;

bool invalid_buffers(
    const float* input,
    int sample_count,
    float* output)
{
    return input == nullptr ||
           output == nullptr ||
           sample_count < 0;
}
}

SKYROOF_EXPORT int hamnoise_skyroof_abi_version()
{
    return kAbiVersion;
}

SKYROOF_EXPORT int hamnoise_skyroof_sample_rate()
{
    return DENOISE_SAMPLE_RATE;
}

SKYROOF_EXPORT int hamnoise_skyroof_classic_hop_length()
{
    return DENOISE_HOP_LENGTH;
}

SKYROOF_EXPORT int hamnoise_skyroof_v2_hop_length()
{
    return kV2HopLength;
}

SKYROOF_EXPORT int hamnoise_skyroof_v2_latency_samples()
{
    return kV2HopLength * kV2LatencyHops;
}

// Batch helper around HamNoise's classic CW stream. The upstream batch
// routine already strips its warm-up latency by writing only produced hops
// to the beginning of the destination and flushing the OLA tail.
SKYROOF_EXPORT int hamnoise_skyroof_process_classic(
    const float* input,
    int sample_count,
    float* output)
{
    if (invalid_buffers(input, sample_count, output))
        return -100;
    if (sample_count == 0)
        return 0;

    return denoise_process_3k2_f32(
        &k_denoise_model,
        input,
        static_cast<std::size_t>(sample_count),
        output);
}

// CW V2 currently uses process-global state in upstream HamNoise. SkyRoof
// performs complete immutable lane windows serially through this bridge, and
// the mutex also makes the ABI safe if a future caller invokes it concurrently.
// The web worklet reports three hops of algorithmic latency. For an offline
// decode window we flush that delay and advance the processed stream by the
// same amount so sample 0 remains aligned with sample 0 of the dry lane.
SKYROOF_EXPORT int hamnoise_skyroof_process_v2(
    const float* input,
    int sample_count,
    float* output)
{
    if (invalid_buffers(input, sample_count, output))
        return -100;
    if (sample_count == 0)
        return 0;

    std::lock_guard<std::mutex> lock(g_v2_mutex);

    int status = cw_v2_init();
    if (status != 0)
        return status;
    cw_v2_reset();

    const int input_hops =
        (sample_count + kV2HopLength - 1) /
        kV2HopLength;
    const int total_hops =
        input_hops + kV2LatencyHops;
    const int total_samples =
        total_hops * kV2HopLength;

    std::vector<float> raw(
        static_cast<std::size_t>(total_samples),
        0.0f);
    float hop_in[kV2HopLength] = {};
    float hop_out[kV2HopLength] = {};
    float gains[kV2GainCapacity] = {};

    for (int hop = 0; hop < total_hops; ++hop)
    {
        std::fill(
            std::begin(hop_in),
            std::end(hop_in),
            0.0f);

        const int input_start =
            hop * kV2HopLength;
        if (input_start < sample_count)
        {
            const int available =
                std::min(
                    kV2HopLength,
                    sample_count - input_start);
            std::memcpy(
                hop_in,
                input + input_start,
                static_cast<std::size_t>(available) *
                    sizeof(float));
        }

        status = cw_v2_process_hop(
            1,
            hop_in,
            hop_out,
            gains);
        if (status < 0)
            return status;

        std::memcpy(
            raw.data() +
                static_cast<std::size_t>(hop) *
                    kV2HopLength,
            hop_out,
            sizeof(hop_out));
    }

    const int latency_samples =
        kV2LatencyHops * kV2HopLength;
    for (int i = 0; i < sample_count; ++i)
    {
        const int source = i + latency_samples;
        output[i] =
            source < total_samples
                ? raw[static_cast<std::size_t>(source)]
                : 0.0f;
    }

    return 0;
}
