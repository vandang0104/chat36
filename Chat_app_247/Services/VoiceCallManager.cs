using Chat_app_247.Models;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIPSorceryMedia.Windows;
using SIPSorceryMedia.Abstractions;

namespace Chat_app_247.Services
{
    public class VoiceCallManager
    {
        private RTCPeerConnection _pc;
        private FirebaseDatabaseService _firebaseService;
        private string _currentCallId;
        private string _currentUserToken;
        private CancellationTokenSource _listenCts;
        private WindowsAudioEndPoint _winAudio;

        public event Action<string> OnCallStatusChanged;

        public VoiceCallManager(FirebaseDatabaseService firebaseService)
        {
            _firebaseService = firebaseService;
        }

        private void InitializePeerConnection()
        {
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
                if (_pc.signalingState == RTCSignalingState.have_local_offer ||
                  _pc.signalingState == RTCSignalingState.have_remote_offer)
                {
                    await _firebaseService.SendSignalAsync(_currentCallId, $"candidate_{Guid.NewGuid()}", new SignalingMessage
                    {
                        Type = "candidate",
                        Candidate = candidate.candidate,
                        SdpMid = candidate.sdpMid,
                        SdpMLineIndex = candidate.sdpMLineIndex
                    }, _currentUserToken);
                }
            };

            _pc.onconnectionstatechange += (state) =>
            {
                OnCallStatusChanged?.Invoke($"Trạng thái: {state}");

                if (state == RTCPeerConnectionState.connected)
                {
                    _winAudio.Start();
                }
                else if (state == RTCPeerConnectionState.failed)
                {
                    OnCallStatusChanged?.Invoke("Kết nối thất bại. Kiểm tra mạng.");
                    EndCall();
                }
                else if (state == RTCPeerConnectionState.closed)
                {
                    EndCall();
                }
            };

            try
            {
                _winAudio = new WindowsAudioEndPoint(new AudioEncoder());

                var audioTrack = new MediaStreamTrack(_winAudio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv);
                _pc.addTrack(audioTrack);

                _pc.OnAudioFormatsNegotiated += (formats) => _winAudio.SetAudioSinkFormat(formats[0]);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không tìm thấy Microphone: " + ex.Message);
            }
        }

        public async Task StartCallAsync(string callId, string token)
        {
            _currentCallId = callId;
            _currentUserToken = token;

            InitializePeerConnection();

            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer);

            await _firebaseService.SendSignalAsync(_currentCallId, "offer", new SignalingMessage
            {
                Type = "offer",
                Sdp = offer.sdp
            }, _currentUserToken);

            StartListeningSignaling();
            OnCallStatusChanged?.Invoke("Đang gọi... (Chờ nghe máy)");
        }

        public async Task JoinCallAsync(string callId, string token)
        {
            _currentCallId = callId;
            _currentUserToken = token;

            InitializePeerConnection();
            StartListeningSignaling();

            OnCallStatusChanged?.Invoke("Đang kết nối... (Tìm tín hiệu)");
        }

        private void StartListeningSignaling()
        {
            _listenCts = new CancellationTokenSource();
            Task.Run(() => _firebaseService.ListenToCallSignals(_currentCallId, _currentUserToken, HandleSignal, _listenCts.Token));
        }

        private async void HandleSignal(string key, SignalingMessage msg)
        {
            if (_pc == null) return;

            if (msg.Type == "offer" && _pc.signalingState == RTCSignalingState.stable)
            {
                _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = msg.Sdp });

                var answer = _pc.createAnswer(null);
                await _pc.setLocalDescription(answer);

                await _firebaseService.SendSignalAsync(_currentCallId, "answer", new SignalingMessage
                {
                    Type = "answer",
                    Sdp = answer.sdp
                }, _currentUserToken);
            }
            else if (msg.Type == "answer" && _pc.signalingState == RTCSignalingState.have_local_offer)
            {
                _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = msg.Sdp });
            }
            else if (msg.Type == "candidate")
            {
                _pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = msg.Candidate,
                    sdpMid = msg.SdpMid,
                    sdpMLineIndex = (ushort)msg.SdpMLineIndex
                });
            }
            else if (msg.Type == "bye")
            {
                EndCall();
            }
        }

        public async void EndCall()
        {
            try
            {
                if (_pc != null)
                {
                    _pc.Close("User ended call");
                    _pc = null;
                }
                if (_winAudio != null)
                {
                    await _winAudio.Close();
                    _winAudio = null;
                }
                _listenCts?.Cancel();

                await _firebaseService.SendSignalAsync(_currentCallId, "bye", new SignalingMessage { Type = "bye" }, _currentUserToken);
            }
            catch { }

            OnCallStatusChanged?.Invoke("Kết thúc.");
        }
    }

}