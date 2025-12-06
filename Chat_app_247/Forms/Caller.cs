using Chat_app_247.Services;
using System;
using System.Windows.Forms;

namespace Chat_app_247.Forms
{
    public partial class Caller : Form
    {
        private VoiceCallManager _callManager;
        private string _callId;
        private string _token;
        private bool _isCaller;
        private bool _isClosing = false;

        public Caller(string callId, string token, bool isCaller)
        {
            InitializeComponent();
            _callId = callId;
            _token = token;
            _isCaller = isCaller;

            var firebaseService = new FirebaseDatabaseService();
            _callManager = new VoiceCallManager(firebaseService);

            _callManager.OnCallStatusChanged += UpdateStatus;
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
                catch (ObjectDisposedException)
                {
                    // Form đã dispose
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
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
                        MessageBox.Show(status, "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        this.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi UpdateStatus: {ex.Message}");
            }
        }

        private async void Caller_Load(object sender, EventArgs e)
        {
            try
            {
                _callManager.OnCallStatusChanged += (status) =>
                {
                    if (this.InvokeRequired) this.Invoke(new Action(() => lblStatus.Text = status));
                    else lblStatus.Text = status;
                };

                if (_isCaller)
                {
                    await _callManager.StartCallAsync(_callId, _token);
                    lblStatus.Text = "Đang gọi...";
                }
                else
                {
                    await _callManager.JoinCallAsync(_callId, _token);
                    lblStatus.Text = "Đang kết nối...";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}");
            }
        }

        private void guna2Button1_Click(object sender, EventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            try
            {
                _callManager?.EndCall();
            }
            catch { }

            this.Close();
        }
        private void Caller_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_isClosing)
            {
                _isClosing = true;
                try
                {
                    _callManager?.EndCall();
                }
                catch { }
            }

            // Hủy đăng ký event
            if (_callManager != null)
            {
                _callManager.OnCallStatusChanged -= UpdateStatus;
                _callManager.Dispose();
                _callManager = null;
            }
        }
    }
}