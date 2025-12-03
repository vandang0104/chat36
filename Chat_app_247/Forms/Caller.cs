using Chat_app_247.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Chat_app_247.Forms
{
    public partial class Caller : Form
    {
        private VoiceCallManager _callManager;
        private string _callId;
        private string _token;
        private bool _isCaller; 

        // Constructor nhận thêm tham số
        public Caller(string callId, string token, bool isCaller)
        {
            InitializeComponent();
            _callId = callId;
            _token = token;
            _isCaller = isCaller;

            // Khởi tạo Service
            var firebaseService = new FirebaseDatabaseService(); // Hoặc lấy từ Singleton nếu có
            _callManager = new VoiceCallManager(firebaseService);

            // Đăng ký sự kiện cập nhật giao diện
            _callManager.OnCallStatusChanged += UpdateStatus;
        }

        private void UpdateStatus(string status)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(UpdateStatus), status);
                return;
            }
            this.Text = status; 

            if (status == "Kết thúc.")
            {
                MessageBox.Show("Cuộc gọi đã kết thúc.");
                this.Close();
            }
        }

        private async void Caller_Load(object sender, EventArgs e)
        {
            try
            {
                if (_isCaller)
                {
                    await _callManager.StartCallAsync(_callId, _token);
                }
                else
                {
                    await _callManager.JoinCallAsync(_callId, _token);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi cuộc gọi: " + ex.Message);
                this.Close();
            }
        }

        // Sự kiện nút tắt máy 
        private void guna2Button1_Click(object sender, EventArgs e)
        {
            _callManager.EndCall();
            this.Close();
        }

        // Đảm bảo dọn dẹp khi đóng form
        private void Caller_FormClosing(object sender, FormClosingEventArgs e)
        {
            _callManager.EndCall();
        }
    }
}
