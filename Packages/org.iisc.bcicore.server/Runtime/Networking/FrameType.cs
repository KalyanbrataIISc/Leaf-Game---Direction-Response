// ============================================================================
//  FrameType.cs — Mirrors the Python server constants exactly.
//  Frame type byte values sent in every 9-byte header by the ESP32.
// ============================================================================
namespace BciCore
{
    public enum FrameType : byte
    {
        Hello    = 0x00,  // config handshake (fs, ch, uv/count, FIR length, DC_R, montage)
        Raw      = 0x01,  // 32 x int32 ADC counts   (+ optional uint16 marker)
        Proc     = 0x02,  // 32 x float32 filtered µV (+ optional uint16 marker)
        Analysis = 0x03,  // 8  x float32 alpha band power
        Ssvep    = 0x04,  // powerA[8], snrA[8], powerB[8], snrB[8]  (all float32)
        Nf       = 0x05,  // smi_14gt18, smi_18gt14, [smi_14gt18_shaped, smi_18gt14_shaped] + sampleCount
        Health   = 0x06,  // board_ms, drops, heap, marker  (+extended: bad, miss, dspMaxUs)
        Marker   = 0x07,  // uint16 marker latched by board
        Cmd      = 0x08,  // marker command: PC → ESP32
        Ml       = 0x09,  // pred_class (byte), confidence (float32), latency_ms (float32)
    }
}
