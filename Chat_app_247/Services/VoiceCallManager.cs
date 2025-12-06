using Chat_app_247.Models;
using Newtonsoft.Json.Linq;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Chat_app_247.Services
{
    public class VoiceCallManager : IDisposable
    {
        private RTCPeerConnection _pc;
        private readonly FirebaseDatabaseService _firebaseService;
        private string _currentCallId;
        private string _currentUserToken;
        private CancellationTokenSource _listenCts;
        private WindowsAudioEndPoint _winAudio;
        private string _currentUserId;
        // Thêm lock để tránh race condition
        private readonly object _lockObj = new object();
        private bool _isDisposed = false;

        // Buffer ICE candidates nếu chưa có remote description
        private readonly List<RTCIceCandidateInit> _pendingCandidates = new List<RTCIceCandidateInit>();

        public event Action<string> OnCallStatusChanged;

        public VoiceCallManager(FirebaseDatabaseService firebaseService)
        {
            _firebaseService = firebaseService;
        }

        private void InitializePeerConnection()
        {
            // GIỮ NGUYÊN CẤU HÌNH STUN CỦA BẠN
            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                    new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
                }
            };

            _pc = new RTCPeerConnection(config);

            _pc.onicecandidate += async (candidate) =>
            {
                try
                {
                    // Chỉ gửi Candidate khi đã có CallId và Token (tránh gửi rác)
                    if (!string.IsNullOrEmpty(_currentCallId) &&
                        _pc.localDescription != null &&
                        !string.IsNullOrEmpty(candidate.candidate))
                    {
                        await _firebaseService.SendSignalAsync(
                            _currentCallId,
                            $"candidate_{Guid.NewGuid()}",
                            new SignalingMessage
                            {
                                Type = "candidate",
                                Candidate = candidate.candidate,
                                SdpMid = candidate.sdpMid,
                                SdpMLineIndex = candidate.sdpMLineIndex
                            },
                            _currentUserToken);
                    }
                }
                catch (Exception ex)
                {
                    // Log lỗi thay vì crash app
                    System.Diagnostics.Debug.WriteLine($"Lỗi gửi ICE candidate: {ex.Message}");
                }
            };


            _pc.onconnectionstatechange += (state) =>
            {
                try
                {
                    // Gọi về UI thread an toàn hơn thông qua event
                    OnCallStatusChanged?.Invoke($"Trạng thái: {state}");

                    if (state == RTCPeerConnectionState.connected)
                    {
                        _winAudio?.Start();
                    }
                    else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
                    {
                        EndCall();
                    }
                }
                catch { }
            };

            try
            {
                _winAudio = new WindowsAudioEndPoint(new AudioEncoder());

                // Kiểm tra có audio source không
                var formats = _winAudio.GetAudioSourceFormats();
                if (formats == null || formats.Count == 0)
                {
                    throw new Exception("Không tìm thấy microphone hoặc loa");
                }

                var audioTrack = new MediaStreamTrack(
                    _winAudio.GetAudioSourceFormats(),
                    MediaStreamStatusEnum.SendRecv);
                _pc.addTrack(audioTrack);

                _pc.OnAudioFormatsNegotiated += (formats) =>
                {
                    try { _winAudio?.SetAudioSinkFormat(formats[0]); } catch { }
                };
            }
            catch (Exception ex)
            {
                OnCallStatusChanged?.Invoke($"Lỗi Mic/Loa: {ex.Message}");
            }
        }

        public async Task StartCallAsync(string callId, string token, string userId)
        {
            _currentCallId = userId;

            lock (_lockObj)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(VoiceCallManager));
            }

            try
            {
                _currentCallId = callId;
                _currentUserToken = token;

                InitializePeerConnection();

                RTCSessionDescriptionInit offer;
                try
                {
                    offer = _pc.createOffer(null);
                    await _pc.setLocalDescription(offer);
                }
                catch (Exception ex)
                {
                    OnCallStatusChanged?.Invoke($"Không thể tạo offer: {ex.Message}");
                    EndCall();
                    return;
                }

                // Gửi offer lên Firebase
                await _firebaseService.SendSignalAsync(
                    _currentCallId,
                    "offer",
                    new SignalingMessage
                    {
                        Type = "offer",
                        Sdp = offer.sdp,
                        SenderId = userId
                    },
                    _currentUserToken);

                StartListeningSignaling();
                OnCallStatusChanged?.Invoke("Đang gọi... (Chờ nghe máy)");
            }
            catch (Exception ex)
            {
                OnCallStatusChanged?.Invoke($"Lỗi bắt đầu cuộc gọi: {ex.Message}");
                EndCall();
                throw;
            }
        }

        public async Task JoinCallAsync(string callId, string token)
        {
            lock (_lockObj)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(VoiceCallManager));
            }

            try
            {
                _currentCallId = callId;
                _currentUserToken = token;

                InitializePeerConnection();
                StartListeningSignaling();

                OnCallStatusChanged?.Invoke("Đang kết nối... (Tìm tín hiệu)");
            }
            catch (Exception ex)
            {
                OnCallStatusChanged?.Invoke($"Lỗi tham gia cuộc gọi: {ex.Message}");
                EndCall();
                throw;
            }
        }

        private void StartListeningSignaling()
        {
            _listenCts?.Cancel();
            _listenCts?.Dispose();
            _listenCts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    await _firebaseService.ListenToCallSignals(
                        _currentCallId,
                        _currentUserToken,
                        HandleSignalAsync,
                        _listenCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Bình thường khi cancel
                }
                catch (Exception ex)
                {
                    OnCallStatusChanged?.Invoke($"Lỗi lắng nghe tín hiệu: {ex.Message}");
                }
            }, _listenCts.Token);
        }

        private async void HandleSignalAsync(string key, SignalingMessage msg)
        {
            if (msg.SenderId == _currentUserId)
            {
                return;
            }

            RTCPeerConnection pc;
            lock (_lockObj)
            {
                if (msg.Type?.ToLower() == "offer")
                {
                    if (_pc == null || _pc.signalingState == RTCSignalingState.closed)
                    {
                        System.Diagnostics.Debug.WriteLine("DEBUG: PC cũ/đóng, đang tạo PC mới cho cuộc gọi đến...");
                        InitializePeerConnection();
                    }
                }
                if (_isDisposed || _pc == null || _pc.signalingState == RTCSignalingState.closed) return;
                    
                pc = _pc;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"Nhận tín hiệu loại: {msg.Type} từ {msg.SenderId}");

                switch (msg.Type?.ToLower())
                {
                    case "offer":
                        // Receiver xử lý Offer
                        // Chỉ xử lý khi đang ở trạng thái Stable (sẵn sàng)
                        if (pc.signalingState == RTCSignalingState.stable)
                        {
                            System.Diagnostics.Debug.WriteLine("DEBUG: Receiver đang xử lý Offer...");

                            // 1. Set Remote Description
                            pc.setRemoteDescription(new RTCSessionDescriptionInit
                            {
                                type = RTCSdpType.offer,
                                sdp = msg.Sdp
                            });

                            // 2. Tạo Answer
                            var answer = pc.createAnswer(null);

                            // 3. Set Local Description
                            await pc.setLocalDescription(answer);

                            // 4. Gửi Answer lên Firebase
                            // Lưu ý: Nhớ kèm SenderId là ID của mình để bên kia lọc được
                            await _firebaseService.SendSignalAsync(
                                _currentCallId,
                                "answer",
                                new SignalingMessage
                                {
                                    Type = "answer",
                                    Sdp = answer.sdp,
                                    SenderId = _currentUserId // <-- Quan trọng
                                },
                                _currentUserToken // Token của đối phương (Caller)
                            );

                            ProcessPendingCandidates(pc);
                        }
                        else
                        {
                            // Nhờ bước lọc SenderId ở đầu, Caller sẽ không bao giờ chạy vào đây nữa -> Hết lỗi have_local_offer
                            System.Diagnostics.Debug.WriteLine($"Cảnh báo: Nhận Offer nhưng trạng thái không phải Stable: {pc.signalingState}");
                        }
                        break;

                    case "answer":
                        // Caller xử lý Answer từ Receiver
                        if (pc.signalingState == RTCSignalingState.have_local_offer)
                        {
                            System.Diagnostics.Debug.WriteLine("DEBUG: Caller đang xử lý Answer...");
                            pc.setRemoteDescription(new RTCSessionDescriptionInit
                            {
                                type = RTCSdpType.answer,
                                sdp = msg.Sdp
                            });
                            ProcessPendingCandidates(pc);
                        }
                        break;

                    case "candidate":
                        if (pc.remoteDescription != null && pc.signalingState != RTCSignalingState.closed)
                        {
                            var iceCandidate = new RTCIceCandidateInit
                            {
                                candidate = msg.Candidate,
                                sdpMid = msg.SdpMid,
                                sdpMLineIndex = (ushort)msg.SdpMLineIndex
                            };
                            pc.addIceCandidate(iceCandidate);
                        }
                        else
                        {
                            // Lưu vào hàng đợi nếu RemoteDescription chưa sẵn sàng
                            lock (_pendingCandidates)
                            {
                                var iceCandidate = new RTCIceCandidateInit
                                {
                                    candidate = msg.Candidate,
                                    sdpMid = msg.SdpMid,
                                    sdpMLineIndex = (ushort)msg.SdpMLineIndex
                                };
                                _pendingCandidates.Add(iceCandidate);
                            }
                        }
                        break;

                    case "bye":
                    case "declined":
                        EndCall();
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi HandleSignal: {ex.Message}");
            }
        }

        private void ProcessPendingCandidates(RTCPeerConnection pc)
        {
            lock (_pendingCandidates)
            {
                foreach (var candidate in _pendingCandidates)
                {
                    try { pc.addIceCandidate(candidate); } catch { }
                }
                _pendingCandidates.Clear();
            }
        }

        public async void EndCall()
        {
            lock (_lockObj)
            {
                if (_isDisposed) return;
                _isDisposed = true;
            }

            try
            {
                // Gửi bye signal
                if (!string.IsNullOrEmpty(_currentCallId))
                {
                    try
                    {
                        await _firebaseService.SendSignalAsync(
                            _currentCallId,
                            "bye",
                            new SignalingMessage { Type = "bye" },
                            _currentUserToken);
                    }
                    catch { }
                }

                // Cancel listening
                _listenCts?.Cancel();

                // Cleanup audio
                if (_winAudio != null)
                {
                    try
                    {
                        await _winAudio.CloseAudio();
                    }
                    catch { }
                    _winAudio = null;
                }

                // Cleanup peer connection
                if (_pc != null)
                {
                    try
                    {
                        _pc.Close("User ended call");
                    }
                    catch { }
                    _pc = null;
                }

                OnCallStatusChanged?.Invoke("Kết thúc.");
            }
            catch (Exception ex)
            {
                OnCallStatusChanged?.Invoke($"Lỗi kết thúc cuộc gọi: {ex.Message}");
            }
        }

        public void Dispose()
        {
            EndCall();
            _listenCts?.Dispose();
            _listenCts = null;
            GC.SuppressFinalize(this);
        }
    }
}