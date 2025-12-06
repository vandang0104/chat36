using Chat_app_247.Services;
using FireSharp.Interfaces;
using System;
using System.Windows.Forms;

namespace Chat_app_247.Forms
{
    public partial class InComingCall : Form
    {
        private VoiceCallManager _callManager;
        private string _callId;
        private string _token;
        private string _callerName;
        private bool _isClosing = false;

        private IFirebaseClient _firebaseClient;
        private string _myUserId;

        public InComingCall(string callId, string callerName, string token,
            IFirebaseClient firebaseClient = null, string myUserId = null)
        {
            InitializeComponent();
            _callId = callId;
            _token = token;
            _callerName = callerName;
            _firebaseClient = firebaseClient;
            _myUserId = myUserId;

            var firebaseService = new FirebaseDatabaseService();
            _callManager = new VoiceCallManager(firebaseService);

            _callManager.OnCallStatusChanged += UpdateStatus;

            // Hiển thị tên người gọi
            lblCallerName.Text = callerName;
            lblStatus.Text = "Đang gọi đến...";
        }

        private void UpdateStatus(string status)
        {
            if (this.IsDisposed || _isClosing) return;

            if (InvokeRequired)
            {
                try
                {
                    Invoke(new Action<string>(UpdateStatus), status);
                }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                return;
            }

            try
            {
                this.Text = status;
                lblStatus.Text = status;

                if (status.Contains("Kết thúc") ||
                    status.Contains("từ chối") ||
                    status.Contains("thất bại"))
                {
                    if (!_isClosing)
                    {
                        _isClosing = true;
                        this.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi UpdateStatus: {ex.Message}");
            }
        }
        private async void btnAccept_Click(object sender, EventArgs e)
        {
            try
            {
                // Ẩn nút để tránh click nhiều lần
                btnAccept.Enabled = false;
                btnDecline.Enabled = false;


                if (_firebaseClient != null && !string.IsNullOrEmpty(_myUserId))
                {
                    try
                    {
                        await _firebaseClient.DeleteAsync($"Users/{_myUserId}/incoming_call");
                    }
                    catch { }
                }

                // Chuyển sang form Caller
                this.Hide();
                Caller callerForm = new Caller(_callId, _token, false,_myUserId);
                callerForm.FormClosed += (s, args) => this.Close();
                callerForm.Show();

                if (_callManager != null)
                {
                    _callManager.OnCallStatusChanged -= UpdateStatus;
                    _callManager.Dispose();
                    _callManager = null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi kết nối: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        private async void btnDecline_Click(object sender, EventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            try
            {
                // Gửi signal "declined"
                var firebaseService = new FirebaseDatabaseService();
                await firebaseService.SendSignalAsync(
                    _callId,
                    "declined",
                    new Models.SignalingMessage { Type = "declined" },
                    _token);

                if (_firebaseClient != null && !string.IsNullOrEmpty(_myUserId))
                {
                    try
                    {
                        await _firebaseClient.DeleteAsync($"Users/{_myUserId}/incoming_call");
                    }
                    catch { }
                }

                _callManager?.EndCall();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi decline: {ex.Message}");
            }

            this.Close();
        }

        private void InComingCall_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_isClosing)
            {
                _isClosing = true;

                if (_firebaseClient != null && !string.IsNullOrEmpty(_myUserId))
                {
                    try
                    {
                        _firebaseClient.DeleteAsync($"Users/{_myUserId}/incoming_call").Wait(1000);
                    }
                    catch { }
                }

                try
                {
                    _callManager?.EndCall();
                }
                catch { }
            }

            if (_callManager != null)
            {
                _callManager.OnCallStatusChanged -= UpdateStatus;
                _callManager.Dispose();
                _callManager = null;
            }
        }
    }
}