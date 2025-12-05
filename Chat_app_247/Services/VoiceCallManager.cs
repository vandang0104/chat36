using Chat_app_247.Models;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Windows;
using SIPSorceryMedia.Abstractions;
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

        public async Task StartCallAsync(string callId, string token)
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
                        Sdp = offer.sdp
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
            RTCPeerConnection pc;
            lock (_lockObj)
            {
                if (_isDisposed || _pc == null) return;
                pc = _pc; // Local copy để tránh race condition
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"Nhận tín hiệu: {msg.Type}");
                switch (msg.Type?.ToLower()) // Chuyển về lowercase để so sánh an toàn
                {
                    case "offer":
                        // Chỉ nhận offer khi chưa kết nối
                        if (pc.signalingState == RTCSignalingState.stable)
                        {
                            pc.setRemoteDescription(new RTCSessionDescriptionInit
                            {
                                type = RTCSdpType.offer,
                                sdp = msg.Sdp
                            });

                            var answer = pc.createAnswer(null);
                            await pc.setLocalDescription(answer);

                            await _firebaseService.SendSignalAsync(
                                _currentCallId,
                                "answer",
                                new SignalingMessage
                                {
                                    Type = "answer",
                                    Sdp = answer.sdp
                                },
                                _currentUserToken);

                            // Xử lý candidate bị pending
                            ProcessPendingCandidates(pc);
                        }
                        break;

                    case "answer":
                        if (pc.signalingState == RTCSignalingState.have_local_offer)
                        {
                            pc.setRemoteDescription(new RTCSessionDescriptionInit
                            {
                                type = RTCSdpType.answer,
                                sdp = msg.Sdp
                            });

                            ProcessPendingCandidates(pc);
                        }
                        break;

                    case "candidate":
                        var iceCandidate = new RTCIceCandidateInit
                        {
                            candidate = msg.Candidate,
                            sdpMid = msg.SdpMid,
                            sdpMLineIndex = (ushort)msg.SdpMLineIndex
                        };

                        if (pc.remoteDescription != null)
                        {
                            pc.addIceCandidate(iceCandidate);
                        }
                        else
                        {
                            lock (_pendingCandidates)
                            {
                                _pendingCandidates.Add(iceCandidate);
                            }
                        }
                        break;

                    case "bye":
                    case "declined":
                        EndCall();
                        OnCallStatusChanged?.Invoke("Cuộc gọi kết thúc.");
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