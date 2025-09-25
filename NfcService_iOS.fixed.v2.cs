// NfcService_iOS.fixed.v2.cs
// iOS CoreNFC - BAC + Secure Messaging + EF.COM read (with SM/Plain fallbacks)
// Target: net8.0-ios
// This v2 revision adds:
// 1) _lastSmHeaderMac caching and usage in SM response MAC verification
// 2) SmSelectByFidTry() helper to try multiple SM SELECT variants
// 3) PlainReadBinaryFallback() to read EF.COM fully in plain mode if SM SELECT fails
// 4) Updated ReadLdsAsync() to use the new strategy (SM attempts → plain fallback)
//
// NOTE: This remains a BAC-only demo (no PACE). Some eID cards may require PACE.

using CoreNFC;
using Foundation;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Copoint.NFC.TR.Platforms.iOS
{
    public interface INfcLogger { void Log(string msg); }
    public class ConsoleLogger : INfcLogger { public void Log(string msg) => Console.WriteLine(msg); }

    public class NfcService_iOS
        : CoreNFC.NFCTagReaderSessionDelegate,
          INfcService   // Project-shared interface (assumed present in shared code)
    {
        private INfcLogger _logger = new ConsoleLogger();
        public event Action<string>? OnLog;
        public event Action<byte[]?>? OnFileRead;

        private void Log(string s)
        {
            var line = $"[NFC] {s}";
            _logger?.Log(line);
            OnLog?.Invoke(line);
        }
        public void SetLogger(INfcLogger logger) => _logger = logger ?? new ConsoleLogger();

        private NFCTagReaderSession? _session;
        private INFCIso7816Tag? _iso;

        // ---- BAC keys ----
        private byte[]? _kSeed16;           // SHA1(mrzInfo)[0..15]
        private byte[]? _kEnc16, _kMac16;   // BAC Kenc/Kmac (DES parity fixed)

        // ---- SM keys & counter ----
        private byte[]? _ksEnc16, _ksMac16; // SM KSenc/KSmac (DES parity fixed)
        private byte[]? _ssc8;              // Send Sequence Counter (8B)


        // ---- EA session keys ----
        private byte[]? _kIfd16;        // 16B (terminal)
        private byte[]? _kIcc16;        // 16B (card)

        // ---- EA randoms ----
        private byte[]? _rndIfd;            // 8B
        private byte[]? _rndIcc;            // 8B

        // ---- NEW: cache the last protected APDU header (CLA|INS|P1|P2) for response MAC verification
        private byte[] _lastSmHeaderMac = Array.Empty<byte>();

        // ------------ MRZ / BAC key derivation ------------
        private bool TryParseTD1FromLines(
            string? l1, string? l2, string? l3,
            out string docNo, out char docCd, out string dob, out char dobCd, out string exp, out char expCd)
        {
            docNo = dob = exp = string.Empty; docCd = dobCd = expCd = '0';
            if (string.IsNullOrWhiteSpace(l1) || string.IsNullOrWhiteSpace(l2)) return false;

            l1 = l1.Trim().ToUpperInvariant();
            l2 = l2.Trim().ToUpperInvariant();
            if (l1.Length < 15 || l2.Length < 15) return false;

            // TD1 slices (0-based)
            docNo = l1.Substring(5, 9);   // 5..13
            docCd = l1[14];               // 14
            dob = l2.Substring(0, 6);     // 0..5
            dobCd = l2[6];                // 6
            exp = l2.Substring(8, 6);     // 8..13
            expCd = l2[14];               // 14

            bool digitsOK = char.IsDigit(docCd) && char.IsDigit(dobCd) && char.IsDigit(expCd);
            return digitsOK;
        }

        private void DeriveBacKeysFromMrzInfo(string mrzInfo)
        {
            using var sha = SHA1.Create();
            _kSeed16 = sha.ComputeHash(Encoding.ASCII.GetBytes(mrzInfo)).Take(16).ToArray();

            static byte[] Kdf16(byte[] kseed, uint counter)
            {
                var c = new byte[] {
                    (byte)(counter >> 24), (byte)(counter >> 16),
                    (byte)(counter >>  8), (byte) counter
                };
                var buf = new byte[kseed.Length + 4];
                Buffer.BlockCopy(kseed, 0, buf, 0, kseed.Length);
                Buffer.BlockCopy(c, 0, buf, kseed.Length, 4);
                using var sha1 = SHA1.Create();
                return sha1.ComputeHash(buf).Take(16).ToArray();
            }

            _kEnc16 = FixDesParity(Kdf16(_kSeed16, 1));
            _kMac16 = FixDesParity(Kdf16(_kSeed16, 2));

            Log($"[DEBUG] Kenc={Hex(_kEnc16.AsSpan(0, 4).ToArray())}-**  Kmac={Hex(_kMac16.AsSpan(0, 4).ToArray())}-**");
            Log("BAC keys derived from MRZ (KDF counters 1/2).");
        }

        // ----------------- NFC session -----------------
        public void StartSession(string? mrzLine1 = null, string? mrzLine2 = null, string? mrzLine3 = null)
        {
            if (_session != null)
            {
                Log("Session already open; not starting a new one.");
                return;
            }

            if (TryParseTD1FromLines(mrzLine1, mrzLine2, mrzLine3,
                                     out var doc, out var docCd, out var dob, out var dobCd, out var exp, out var expCd))
            {
                var mrzInfo = $"{doc}{docCd}{dob}{dobCd}{exp}{expCd}";
                Log($"[MRZ] TD1 parsed. mrzInfo(masked)={MaskMrz(doc, docCd, dob, exp)}");
                DeriveBacKeysFromMrzInfo(mrzInfo);
            }
            else
            {
                Log("MRZ TD1 could not be parsed; StartSession called without valid MRZ (no BAC keys).");
            }

            if (!NFCTagReaderSession.ReadingAvailable)
            {
                Log("NFC reading not available or disabled.");
                return;
            }

            _session = new NFCTagReaderSession(NFCPollingOption.Iso14443, this, null)
            {
                AlertMessage = "Lütfen kimlik kartınızı iPhone'un arkasına yaklaştırın…"
            };
            _session.BeginSession();
            Log("NFCTagReaderSession started.");
        }

        public void StopSession()
        {
            _session?.InvalidateSession();
            _session = null;
            Log("Session stopped.");
        }

        [Export("tagReaderSession:didInvalidateWithError:")]
        public void DidInvalidate(NFCTagReaderSession session, NSError error)
        {
            Log($"Session invalidated: {error?.LocalizedDescription ?? "user/OS"}");
            _session = null;
        }

        [Export("tagReaderSession:didDetectTags:")]
        public async void DidDetectTags(NFCTagReaderSession session, INFCTag[] tags)
        {
            try
            {
                if (_kEnc16 is null || _kMac16 is null)
                {
                    Log("BAC keys missing; MRZ is required.");
                    session.InvalidateSession();
                    return;
                }

                var tag = tags?.FirstOrDefault();
                if (tag == null) { Log("Tag NULL"); session.InvalidateSession(); return; }

                if (tag.Type == NFCTagType.Iso7816Compatible)
                {
                    _iso = tag.AsNFCIso7816Tag ?? (tags[0] as INFCIso7816Tag);
                }
                if (_iso == null) { Log("ISO7816 Tag could not be resolved."); session.InvalidateSession(); return; }

                Log($"ISO7816 Tag; Identifier={Hex(_iso.Identifier?.ToArray() ?? Array.Empty<byte>())} HistoricalBytes={Hex(_iso.HistoricalBytes?.ToArray() ?? Array.Empty<byte>())}");

                if (!await ConnectToAsync(session, tags[0])) { Log("Connect failed."); session.InvalidateSession(); return; }
                Log("Tag connected.");

                // 1) SELECT AID (LDS): A0000002471001
                var aid = HexToBytes("A0000002471001");
                var selectOk = await ApduSelectAid(_iso, aid);
                if (!selectOk) { Log("SELECT AID failed."); session.InvalidateSession(); return; }

                // 2) GET CHALLENGE
                var rndIcc = await BacGetChallengeAsync(_iso);
                if (rndIcc == null) { Log("GET CHALLENGE failed."); session.InvalidateSession(); return; }
                _rndIcc = rndIcc;
                Log($"RndICC={Hex(_rndIcc)}");

                // 3) S = RND.IFD || RND.ICC || Kifd
                _rndIfd = RandomBytes(8);
                var kIfd = RandomBytes(16);
                _kIfd16 = kIfd;
                Log($"RndIFD={Hex(_rndIfd)}");
                var S = Concat(_rndIfd, _rndIcc, kIfd); // 32

                // 4) EIFD / MIFD (BAC keys)
                var eifd = TripleDesCbcEncryptNoPad(_kEnc16, S);
                var mifd8 = RetailMacIso9797Alg3(_kMac16, eifd);
                Log($"EA: EIFD (len={eifd.Length}) = {Hex(eifd)}");
                Log($"EA: MIFD (first 8) = {Hex(mifd8)}");

                // 5) EXTERNAL AUTHENTICATE (fallback strategy)
                var bacOk = await BacExternalAuthenticateAsync(_iso, eifd, mifd8);
                if (!bacOk) { Log("BAC EXTERNAL AUTH rejected."); session.InvalidateSession(); return; }

                Log("BAC completed (9000). Starting Secure Messaging…");

                // 6) Init SM and read EF.COM
                InitSM();
                await ReadLdsAsync(); // will OnFileRead(EF.COM) if success

                session.InvalidateSession();
            }
            catch (Exception ex)
            {
                Log($"EXC: {ex}");
                session?.InvalidateSession();
            }
        }

        // --------------- APDU helpers (NFCIso7816Apdu) ---------------
        private async Task<bool> ApduSelectAid(INFCIso7816Tag iso, byte[] aid)
        {
            Log("[SELECT] P2=0x0C Le=256 will be used.");
            var apdu = CreateApdu(0x00, 0xA4, 0x04, 0x0C, aid, 256);
            var r = await SendApdu(iso, apdu, "SELECT AID");
            Log($"SELECT AID → SW={r.SW:X4}, LEN={r.Data.Length}");
            return r.SW == 0x9000;
        }

        private async Task<byte[]?> BacGetChallengeAsync(INFCIso7816Tag iso)
        {
            foreach (var le in new[] { 8, 256 })
            {
                var apdu = CreateApdu(0x00, 0x84, 0x00, 0x00, null, le);
                var r = await SendApdu(iso, apdu, $"GET CHALLENGE (Le={le})");
                if (r.SW == 0x9000 && r.Data.Length >= 8)
                {
                    Log($"GET CHALLENGE → SW=9000, LEN={r.Data.Length}, DATA={Hex(r.Data)}");
                    return r.Data.Take(8).ToArray();
                }
            }
            return null;
        }

        private async Task<bool> BacExternalAuthenticateAsync(INFCIso7816Tag iso, byte[] eifd, byte[] mac8)
        {
            {
                // Try combinations and capture the first successful response
                (bool ok, byte[] data, int sw) res;

                foreach (var le in new int?[] { null, 256 })
                {
                    res = await SendEA_RAW(iso, 0x00, eifd, mac8, le);
                    if (res.ok) goto EA_OK;
                }
                foreach (var le in new int?[] { null, 256 })
                {
                    res = await SendEA_RAW(iso, 0x0C, eifd, mac8, le);
                    if (res.ok) goto EA_OK;
                }
                foreach (var le in new int?[] { null, 256 })
                {
                    res = await SendEA_TLV(iso, 0x00, eifd, mac8, le);
                    if (res.ok) goto EA_OK;
                }
                foreach (var le in new int?[] { null, 256 })
                {
                    res = await SendEA_TLV(iso, 0x0C, eifd, mac8, le);
                    if (res.ok) goto EA_OK;
                }
                return false;

            EA_OK:
                // Parse EA response (expect 7C TLV with 0x80:EICC and optionally 0x86:MAC)
                byte[] t7c;
                if (!TryTlvFind(res.data, 0x7C, out t7c))
                {
                    // Some profiles return 7C value directly
                    t7c = res.data;
                }

                if (!TryTlvFind(t7c, 0x80, out var eicc) || eicc.Length == 0x00)
                {
                    Log("EA parse failed: missing 0x80 (EICC).");
                    return false;
                }

                // Optional EA MAC verify (0x86)
                if (TryTlvFind(t7c, 0x86, out var macEa) && macEa.Length >= 8)
                {
                    // MAC input commonly uses the full 7C structure: [7C | len | 80 | len(EICC) | EICC]
                    var macInput = Concat(new byte[] { 0x7C, (byte)t7c.Length, 0x80, (byte)eicc.Length }, eicc);
                    var macCalc = RetailMacIso9797Alg3(_kMac16!, macInput);
                    if (!macEa.Take(8).SequenceEqual(macCalc.Take(8)))
                    {
                        Log("EA MAC verify failed.");
                        return false;
                    }
                    Log("EA MAC verified.");
                }

                // Decrypt EICC with Kenc, IV=0
                var eiccPlain = TripleDesCbcDecryptNoPadVarIV(_kEnc16!, new byte[8], eicc);
                if (eiccPlain.Length < 32)
                {
                    Log("EA EICC plaintext length invalid.");
                    return false;
                }

                var rndIccPrime = eiccPlain.Take(8).ToArray();
                var rndIfdPrime = eiccPlain.Skip(8).Take(8).ToArray();
                var kicc = eiccPlain.Skip(16).Take(16).ToArray();

                byte[] RotL1(byte[] x) => Concat(x.Skip(1).ToArray(), x.Take(1).ToArray());

                if (_rndIcc == null || _rndIfd == null)
                {
                    Log("EA parse: missing RNDs in state.");
                    return false;
                }

                if (!RotL1(_rndIcc).SequenceEqual(rndIccPrime) || !RotL1(_rndIfd).SequenceEqual(rndIfdPrime))
                {
                    Log("EA rotate check failed.");
                    return false;
                }

                _kIcc16 = kicc;
                Log($"EA OK. KICC set ({Hex(_kIcc16.AsSpan(0, 4).ToArray())}-**).");
                return true;
            }

        }

        private async Task<(bool ok, byte[] data, int sw)> SendEA_TLV(INFCIso7816Tag iso, byte cla, byte[] eifd, byte[] mac8, int? leNullable)
        {
            // 7C 2C  80 20 <EIFD>  86 08 <MAC>
            var tlv = new byte[46];
            int p = 0;
            tlv[p++] = 0x7C; tlv[p++] = 0x2C;
            tlv[p++] = 0x80; tlv[p++] = 0x20; Buffer.BlockCopy(eifd, 0, tlv, p, 0x20); p += 0x20;
            tlv[p++] = 0x86; tlv[p++] = 0x08; Buffer.BlockCopy(mac8, 0, tlv, p, 0x08); p += 0x08;

            var apdu = CreateApdu(cla, 0x82, 0x00, 0x00, tlv, leNullable);
            var r = await SendApdu(iso, apdu, $"EA TLV cla={cla:X2} le={(leNullable.HasValue ? leNullable.Value.ToString() : "∅")}");
            return (r.SW == 0x9000, r.Data, r.SW);
        }

        private async Task<(bool ok, byte[] data, int sw)> SendEA_RAW(INFCIso7816Tag iso, byte cla, byte[] eifd, byte[] mac8, int? leNullable)
        {
            var raw = Concat(eifd, mac8);
            var apdu = CreateApdu(cla, 0x82, 0x00, 0x00, raw, leNullable);
            var r = await SendApdu(iso, apdu, $"EA RAW cla={cla:X2} le={(leNullable.HasValue ? leNullable.Value.ToString() : "∅")}");
            return (r.SW == 0x9000, r.Data, r.SW);
        }

        // ---- APDU creator (NFCIso7816Apdu) ----
        private static NFCIso7816Apdu CreateApdu(byte cla, byte ins, byte p1, byte p2, byte[]? data, int? expectedResponseLength)
        {
            int normLe(int? le) => !le.HasValue ? -1 : (le.Value == 0 ? 256 : le.Value);
            nint exp = (nint)normLe(expectedResponseLength);

            NSData nsData = (data != null && data.Length > 0)
                ? NSData.FromArray(data)
                : new NSData();

            return new NFCIso7816Apdu(cla, ins, p1, p2, nsData, exp);
        }

        private async Task<(byte[] Data, int SW)> SendApdu(INFCIso7816Tag iso, NFCIso7816Apdu apdu, string label)
        {
            Log($">> {label}  CLA={apdu.InstructionClass:X2} INS={apdu.InstructionCode:X2} P1={apdu.P1Parameter:X2} P2={apdu.P2Parameter:X2} Lc={(apdu.Data == null ? 0 : apdu.Data.Length)} Le={(int)apdu.ExpectedResponseLength}");
            var tcs = new TaskCompletionSource<(byte[] Data, int SW)>();
            iso.SendCommand(apdu, (response, sw1, sw2, error) =>
            {
                try
                {
                    if (error != null)
                    {
                        Log($"<< {label} ERROR: {error.LocalizedDescription}");
                        tcs.TrySetResult((Array.Empty<byte>(), 0x6F00));
                        return;
                    }
                    var bytes = response?.ToArray() ?? Array.Empty<byte>();
                    var sw = ((sw1 & 0xFF) << 8) | (sw2 & 0xFF);
                    Log($"<< {label} SW={sw1:X2}{sw2:X2} LEN={bytes.Length}");
                    tcs.TrySetResult((bytes, sw));
                }
                catch (Exception ex)
                {
                    Log($"<< {label} EXC: {ex.Message}");
                    tcs.TrySetResult((Array.Empty<byte>(), 0x6F00));
                }
            });
            return await tcs.Task;
        }

        // ----------------- Crypto helpers -----------------
        private static byte[] TripleDesCbcEncryptNoPad(byte[] kenc16, byte[] data)
        {
            var k24 = new byte[24];
            Buffer.BlockCopy(kenc16, 0, k24, 0, 16);
            Buffer.BlockCopy(kenc16, 0, k24, 16, 8); // K1K2K1
            using var tdes = TripleDES.Create();
            tdes.Mode = CipherMode.CBC;
            tdes.Padding = PaddingMode.None;
            tdes.Key = k24;
            tdes.IV = new byte[8];
            using var enc = tdes.CreateEncryptor();
            return enc.TransformFinalBlock(data, 0, data.Length);
        }

        private static byte[] RetailMacIso9797Alg3(byte[] kmac16, byte[] input)
        {
            var (K1, K2) = SplitK1K2(kmac16);
            var padded = Iso9797M2Pad(input, 8);
            var y = DesCbcEncrypt(K1, new byte[8], padded);
            var last = y.Skip(y.Length - 8).Take(8).ToArray();
            var t = DesEcbDecrypt(K2, last);
            var mac = DesEcbEncrypt(K1, t);
            return mac.Take(8).ToArray();
        }

        private static (byte[] K1, byte[] K2) SplitK1K2(byte[] key16)
        {
            var k1 = FixDesParity(key16.Take(8).ToArray());
            var k2 = FixDesParity(key16.Skip(8).Take(8).ToArray());
            return (k1, k2);
        }

        private static byte[] DesCbcEncrypt(byte[] k8, byte[] iv8, byte[] data)
        {
            using var des = DES.Create();
            des.Mode = CipherMode.CBC;
            des.Padding = PaddingMode.None;
            des.Key = FixDesParity(k8);
            des.IV = iv8 ?? new byte[8];
            using var enc = des.CreateEncryptor();
            return enc.TransformFinalBlock(data, 0, data.Length);
        }

        private static byte[] DesEcbEncrypt(byte[] k8, byte[] block8)
        {
            using var des = DES.Create();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            des.Key = FixDesParity(k8);
            using var enc = des.CreateEncryptor();
            return enc.TransformFinalBlock(block8, 0, 8);
        }

        private static byte[] DesEcbDecrypt(byte[] k8, byte[] block8)
        {
            using var des = DES.Create();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            des.Key = FixDesParity(k8);
            using var dec = des.CreateDecryptor();
            return dec.TransformFinalBlock(block8, 0, 8);
        }

        private static byte[] Iso9797M2Pad(byte[] input, int block)
        {
            var padLen = block - ((input.Length + 1) % block);
            if (padLen == block) padLen = 0;
            var outLen = input.Length + 1 + padLen;
            var output = new byte[outLen];
            Buffer.BlockCopy(input, 0, output, 0, input.Length);
            output[input.Length] = 0x80;
            return output;
        }

        private static byte[] FixDesParity(byte[] key)
        {
            var copy = (byte[])key.Clone();
            for (int i = 0; i < copy.Length; i++)
            {
                byte b = copy[i];
                int ones = 0; for (int k = 0; k < 8; k++) ones += (b >> k) & 1;
                if ((ones % 2) == 0) b ^= 0x01; // odd parity
                copy[i] = b;
            }
            return copy;
        }

        // ----------------- SM (Secure Messaging) -----------------

        private void InitSM()
        {
            if (_kSeed16 == null) throw new InvalidOperationException("Kseed missing");

            byte[] Kdf16(byte[] kseed, uint counter)
            {
                var c = new byte[] { (byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter };
                var buf = new byte[kseed.Length + 4];
                Buffer.BlockCopy(kseed, 0, buf, 0, kseed.Length);
                Buffer.BlockCopy(c, 0, buf, kseed.Length, 4);
                using var sha1 = SHA1.Create();
                return FixDesParity(sha1.ComputeHash(buf).Take(16).ToArray());
            }

            _ksEnc16 = null; _ksMac16 = null; // will derive below

            if (_rndIcc == null || _rndIfd == null) throw new InvalidOperationException("RND missing");
            if (_kIfd16 == null || _kIcc16 == null) throw new InvalidOperationException("KIFD/KICC missing");

            // SM-Seed = SHA1(RND.IFD || RND.ICC || KIFD || KICC)[0..15]
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var seedFull = sha1.ComputeHash(Concat(_rndIfd, _rndIcc, _kIfd16, _kIcc16));
                var smSeed16 = seedFull.Take(16).ToArray();
                byte[] C(int n) => new byte[] { 0x00, 0x00, 0x00, (byte)n };
                using var shaA = System.Security.Cryptography.SHA1.Create();
                var a = Concat(smSeed16, C(1));
                var b = Concat(smSeed16, C(2));
                _ksEnc16 = FixDesParity(shaA.ComputeHash(a).Take(16).ToArray());
                using var shaB = System.Security.Cryptography.SHA1.Create();
                _ksMac16 = FixDesParity(shaB.ComputeHash(b).Take(16).ToArray());
            }


            // SSC0 = RND.ICC(last 4) || RND.IFD(last 4)
            _ssc8 = Concat(_rndIcc.Skip(4).Take(4).ToArray(), _rndIfd.Skip(4).Take(4).ToArray());

            Log($"[SM] KSenc/KSmac ready. SSC={Hex(_ssc8)}");
        }

        private void SscIncrement()
        {
            if (_ssc8 == null) throw new InvalidOperationException("SSC missing.");
            for (int i = _ssc8.Length - 1; i >= 0; i--)
            {
                if (++_ssc8[i] != 0) break;
            }
        }

        private static byte[] Tl(int tag, byte[]? val)
        {
            val ??= Array.Empty<byte>();
            if (val.Length <= 0x7F)
                return Concat(new byte[] { (byte)tag, (byte)val.Length }, val);
            if (val.Length <= 0xFF)
                return Concat(new byte[] { (byte)tag, 0x81, (byte)val.Length }, val);
            return Concat(new byte[] { (byte)tag, 0x82, (byte)(val.Length >> 8), (byte)(val.Length & 0xFF) }, val);
        }

        private static bool TryTlvFind(byte[] buf, byte tag, out byte[] value)
        {
            value = Array.Empty<byte>();
            int i = 0;
            while (i < buf.Length)
            {
                var t = buf[i++];
                if (i >= buf.Length) break;
                int len = buf[i++];
                if (len == 0x81)
                {
                    if (i >= buf.Length) break;
                    len = buf[i++];
                }
                else if (len == 0x82)
                {
                    if (i + 1 >= buf.Length) break;
                    len = (buf[i++] << 8) | buf[i++];
                }
                if (len < 0 || i + len > buf.Length) break;
                var v = new byte[len];
                Buffer.BlockCopy(buf, i, v, 0, len);
                i += len;
                if (t == tag)
                {
                    value = v;
                    return true;
                }
            }
            return false;
        }

        private byte[] BuildDo87(byte[]? plain)
        {
            if (plain == null || plain.Length == 0) return Array.Empty<byte>();
            if (_ksEnc16 is null || _ssc8 is null) throw new InvalidOperationException("KSenc/SSC missing.");
            var padded = PadM2(plain);
            var enc = TripleDesCbcEncryptNoPadVarIV(_ksEnc16, _ssc8, padded);
            var val = new byte[1 + enc.Length]; // 0x01 || ENC
            val[0] = 0x01;
            Buffer.BlockCopy(enc, 0, val, 1, enc.Length);
            return Tl(0x87, val);
        }

        private static byte[] PadM2(byte[] data)
        {
            int padLen = 8 - ((data.Length + 1) % 8);
            if (padLen == 8) padLen = 0;
            var res = new byte[data.Length + 1 + padLen];
            Buffer.BlockCopy(data, 0, res, 0, data.Length);
            res[data.Length] = 0x80;
            return res;
        }

        private static byte[] ExpandTo24(byte[] key16or24)
        {
            if (key16or24.Length == 24) return key16or24;
            if (key16or24.Length != 16) throw new ArgumentException("3DES key must be 16 or 24 bytes.");
            var k = new byte[24];
            Buffer.BlockCopy(key16or24, 0, k, 0, 16);
            Buffer.BlockCopy(key16or24, 0, k, 16, 8); // K1 K2 K1
            return k;
        }

        private static byte[] TripleDesCbcEncryptNoPadVarIV(byte[] key16or24, byte[] iv8, byte[] input)
        {
            using var tdes = System.Security.Cryptography.TripleDES.Create();
            tdes.Mode = System.Security.Cryptography.CipherMode.CBC;
            tdes.Padding = System.Security.Cryptography.PaddingMode.None;
            tdes.Key = ExpandTo24(key16or24);
            tdes.IV = iv8;
            using var enc = tdes.CreateEncryptor();
            return enc.TransformFinalBlock(input, 0, input.Length);
        }

        private static byte[] TripleDesCbcDecryptNoPadVarIV(byte[] key16or24, byte[] iv8, byte[] input)
        {
            using var tdes = System.Security.Cryptography.TripleDES.Create();
            tdes.Mode = System.Security.Cryptography.CipherMode.CBC;
            tdes.Padding = System.Security.Cryptography.PaddingMode.None;
            tdes.Key = ExpandTo24(key16or24);
            tdes.IV = iv8;
            using var dec = tdes.CreateDecryptor();
            return dec.TransformFinalBlock(input, 0, input.Length);
        }

        private byte[] BuildDo97(int le)
        {
            if (le < 0) return Array.Empty<byte>();
            if (le == 0 || le > 0xFF) return Tl(0x97, new byte[] { 0x00 }); // 0 → 256
            return Tl(0x97, new byte[] { (byte)le });
        }

        private byte[] BuildDo8E(byte[] headerMac, byte[]? do87, byte[]? do97)
        {
            if (_ksMac16 is null || _ssc8 is null) throw new InvalidOperationException("KSmac/SSC missing.");
            var parts = new List<byte[]>(4) { _ssc8, headerMac };
            if (do87 is { Length: > 0 }) parts.Add(do87);
            if (do97 is { Length: > 0 }) parts.Add(do97);
            var m = Concat(parts.ToArray());
            var mac = RetailMacIso9797Alg3(_ksMac16, m);
            return Tl(0x8E, mac);
        }

        private NFCIso7816Apdu MakeProtectedApdu(byte cla, byte ins, byte p1, byte p2, byte[]? data, int? le)
        {
            if (_ssc8 == null) throw new InvalidOperationException("SSC missing (InitSM required after EA)");
            SscIncrement();

            var do87 = BuildDo87(data);
            var do97 = le.HasValue ? BuildDo97(le.Value) : Array.Empty<byte>();

            var doList = new List<byte[]>();
            if (do87.Length > 0) doList.Add(do87);
            if (do97.Length > 0) doList.Add(do97);

            var claProtected = (byte)(cla | 0x0C);
            var mHeaderMac = new byte[] { claProtected, ins, p1, p2 };
            // NEW: cache last header for response MAC verification
            _lastSmHeaderMac = (byte[])mHeaderMac.Clone();

            var do8e = BuildDo8E(mHeaderMac, do87.Length > 0 ? do87 : null, do97.Length > 0 ? do97 : null);
            doList.Add(do8e);

            var dataField = Concat(doList.ToArray());
            var nsData = dataField.Length > 0 ? NSData.FromArray(dataField) : new NSData();

            return new NFCIso7816Apdu(claProtected, ins, p1, p2, nsData, -1);
        }

        private NFCIso7816Apdu MakeProtectedReadBinary(byte cla, byte p1, byte p2, int le)
        {
            if (_ssc8 == null) throw new InvalidOperationException("SSC missing (InitSM required)");
            SscIncrement();

            var claProt = (byte)(cla | 0x0C);
            byte ins = 0xB0;
            var headerMac = new byte[] { claProt, ins, p1, p2 };
            // NEW: cache last header for response MAC verification
            _lastSmHeaderMac = (byte[])headerMac.Clone();

            var do97 = BuildDo97(le <= 0 ? 256 : le);
            var do8e = BuildDo8E(headerMac, null, do97);

            var payload = Concat(do97, do8e);
            var ns = payload.Length > 0 ? NSData.FromArray(payload) : new NSData();
            return new NFCIso7816Apdu(claProt, ins, p1, p2, ns, -1);
        }

        private async Task<(byte[] Plain, int SW)> SendSmReadBinary(INFCIso7816Tag iso, byte p1, byte p2, int le, string label)
        {
            var apdu = MakeProtectedReadBinary(0x00, p1, p2, le);
            Log($">> {label} (SM) READ P1={p1:X2} P2={p2:X2} Le={(le <= 0 ? 0 : le)}");
            var (resp, sw) = await SendApdu(iso, apdu, label + " [SM]");
            var ok = ParseSmResponse(resp, sw, out var plain);
            if (!ok)
            {
                Log($"{label} [SM] parse/verify failed. SW={sw:X4} RAW={Hex(resp)}");
                return (Array.Empty<byte>(), sw);
            }
            return (plain, 0x9000);
        }

        private bool ParseSmResponse(byte[] resp, int sw, out byte[] plain)
        {
            plain = Array.Empty<byte>();
            if (_ssc8 == null)
            {
                Log("[SM] SSC not initialised before response parse.");
                return false;
            }

            // Increment SSC for the incoming response as mandated by ICAO Doc 9303
            SscIncrement();

            if (sw == 0x9000 && resp.Length == 0)
            {
                // Some tags return everything via DO99 only; treat as empty payload success.
                return true;
            }

            // Response should contain DO87 (optional), DO99 (mandatory), DO8E (mandatory)
            if (!TryTlvFind(resp, 0x99, out var do99) || do99.Length != 2)
            {
                Log("[SM] DO99 missing/invalid.");
                return false;
            }
            var swFromDo99 = (do99[0] << 8) | do99[1];
            if (swFromDo99 != 0x9000)
            {
                Log($"[SM] DO99 SW={swFromDo99:X4} (not 9000).");
                return false;
            }

            if (!TryTlvFind(resp, 0x8E, out var do8e) || do8e.Length != 8)
            {
                Log("[SM] DO8E missing/invalid.");
                return false;
            }

            if (_ksMac16 == null)
            {
                Log("[SM] KSmac missing.");
                return false;
            }

            // Use cached header from the last protected APDU we sent
            byte[] headerMacUsed = (_lastSmHeaderMac != null && _lastSmHeaderMac.Length == 4)
                ? _lastSmHeaderMac
                : new byte[] { 0x0C, 0xB0, 0x00, 0x00 }; // fallback (should not be needed now)

            var blocks = new List<byte[]>() { _ssc8, headerMacUsed };
            if (TryTlvFind(resp, 0x87, out var do87)) blocks.Add(Tl(0x87, do87));
            blocks.Add(Tl(0x99, do99));

            var m = Concat(blocks.ToArray());
            var macCalc = RetailMacIso9797Alg3(_ksMac16, m);
            if (!macCalc.SequenceEqual(do8e.Take(8)))
            {
                Log($"[SM] MAC verify failed. calc={Hex(macCalc)} tag={Hex(do8e)}");
                return false;
            }

            if (_ksEnc16 == null)
            {
                Log("[SM] KSenc missing.");
                return false;
            }

            // Decrypt DO87 if present
            if (do87 != null && do87.Length >= 1)
            {
                if (do87[0] != 0x01) { Log("[SM] DO87 format unexpected."); return false; }
                var enc = do87.Skip(1).ToArray();
                var dec = TripleDesCbcDecryptNoPadVarIV(_ksEnc16, _ssc8, enc);
                plain = UnpadM2(dec);
            }
            else
            {
                plain = Array.Empty<byte>();
            }

            return true;
        }

        private static byte[] UnpadM2(byte[] input)
        {
            int i = input.Length - 1;
            while (i >= 0 && input[i] == 0x00) i--;
            if (i >= 0 && input[i] == 0x80) i--;
            var r = new byte[i + 1];
            Buffer.BlockCopy(input, 0, r, 0, r.Length);
            return r;
        }

        // ----------------- LDS Read demo (EF.COM) -----------------
        private async Task ReadLdsAsync()
        {
            if (_iso == null) throw new InvalidOperationException("ISO tag null");

            var fid = new byte[] { 0x01, 0x1E }; // EF.COM

            // Try SM SELECT with multiple variants first
            int sw = 0;
            sw = await SmSelectByFidTry(_iso, fid, 0x02, 0x0C, 256, true);
            if (sw != 0x9000)
                sw = await SmSelectByFidTry(_iso, fid, 0x02, 0x00, 256, true);
            if (sw != 0x9000)
                sw = await SmSelectByFidTry(_iso, fid, 0x02, 0x0C, null, false); // DO97 off

            if (sw != 0x9000)
            {
                Log($"SM SELECT attempts all failed (last SW={sw:X4}). Trying plain SELECT fallback.");
                // Plain SELECT fallback:
                var r = await SendApdu(_iso, CreateApdu(0x00, 0xA4, 0x02, 0x0C, fid, 256), "SELECT EF.COM (plain)");
                if (r.SW != 0x9000)
                {
                    Log($"SELECT EF.COM failed (plain) SW={r.SW:X4}.");
                    return;
                }
                // Plain read fallback
                await PlainReadBinaryFallback();
                return;
            }

            // If we got here: SM SELECT worked → SM READ BINARY chunks
            var offset = 0;
            var all = new List<byte>(512);
            while (true)
            {
                byte p1 = (byte)((offset >> 8) & 0x7F); // 0x7F mask for short EF
                byte p2 = (byte)(offset & 0xFF);
                var (chunk, swRB) = await SendSmReadBinary(_iso, p1, p2, 0, $"READ BINARY @ {offset}");
                if (swRB != 0x9000) break;
                if (chunk.Length == 0) break;
                all.AddRange(chunk);
                if (chunk.Length < 256) break;
                offset += chunk.Length;
                if (offset > 8192) break; // safety
            }

            var bytes = all.ToArray();
            Log($"EF.COM read {bytes.Length} bytes.");
            OnFileRead?.Invoke(bytes);
        }

        // NEW: SM SELECT helper with options
        private async Task<int> SmSelectByFidTry(INFCIso7816Tag iso, byte[] fid, byte p1, byte p2, int? le = 256, bool includeDo97 = true)
        {
            var apdu = MakeProtectedApdu(0x00, 0xA4, p1, p2, fid, includeDo97 ? le : null);
            var (resp, sw) = await SendApdu(iso, apdu, $"SELECT EF (SM) p1={p1:X2} p2={p2:X2} do97={(includeDo97 ? "Y" : "N")}");
            if (ParseSmResponse(resp, sw, out _))
                return 0x9000;
            return sw;
        }

        // NEW: Plain READ fallback
        private async Task PlainReadBinaryFallback()
        {
            if (_iso == null) { Log("PlainReadBinaryFallback: ISO tag null."); return; }

            var offset = 0;
            var all = new List<byte>(512);
            while (true)
            {
                byte p1 = (byte)((offset >> 8) & 0x7F);
                byte p2 = (byte)(offset & 0xFF);
                var apdu = CreateApdu(0x00, 0xB0, p1, p2, null, 0); // Le=0 -> 256
                var (resp, sw) = await SendApdu(_iso, apdu, $"READ BINARY (plain) @ {offset}");
                if (sw != 0x9000)
                {
                    Log($"READ BINARY (plain) @ {offset} returned SW={sw:X4}. Stopping.");
                    break;
                }
                if (resp == null || resp.Length == 0) break;
                all.AddRange(resp);
                if (resp.Length < 256) break;
                offset += resp.Length;
                if (offset > 16_384) { Log("Reached safety limit for plain read."); break; }
            }
            var bytes = all.ToArray();
            Log($"EF.COM (plain) read {bytes.Length} bytes.");
            OnFileRead?.Invoke(bytes);
        }

        // ----------------- Utilities -----------------
        private static string Hex(byte[]? data)
        {
            if (data == null || data.Length == 0) return "";
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static byte[] HexToBytes(string hex)
        {
            hex = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            if (hex.Length % 2 != 0) throw new ArgumentException("hex length");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            var total = arrays.Where(a => a != null).Sum(a => a!.Length);
            var r = new byte[total];
            int p = 0;
            foreach (var a in arrays)
            {
                if (a == null || a.Length == 0) continue;
                Buffer.BlockCopy(a, 0, r, p, a.Length);
                p += a.Length;
            }
            return r;
        }

        private static byte[] RandomBytes(int len)
        {
            var b = new byte[len];
            RandomNumberGenerator.Fill(b);
            return b;
        }

        private static string MaskMrz(string doc, char docCd, string dob, string exp)
        {
            var left = doc.Length >= 2 ? doc.Substring(0, 2) : doc;
            var right = doc.Length >= 2 ? doc.Substring(doc.Length - 2) : "";
            return $"{left}******{right}{docCd}{dob}*{exp}*";
        }

        private async Task<bool> ConnectToAsync(NFCTagReaderSession session, INFCTag tag)
        {
            var tcs = new TaskCompletionSource<bool>();
            session.ConnectTo(tag, err =>
            {
                if (err != null)
                {
                    Log($"ConnectTo error: {err.LocalizedDescription}");
                    tcs.TrySetResult(false);
                }
                else tcs.TrySetResult(true);
            });
            return await tcs.Task;
        }
    }
}
