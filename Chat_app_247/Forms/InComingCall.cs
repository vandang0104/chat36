using Chat_app_247.Services;
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
    public partial class InComingCall : Form
    {
        private VoiceCallManager _callManager;
        private string _callId;
        private string _token;

        public InComingCall(string callId, string callerName, string token)
        {
            InitializeComponent();
            _callId = callId;
            _token = token;

            var firebaseService = new FirebaseDatabaseService();
            _callManager = new VoiceCallManager(firebaseService);

            _callManager.OnCallStatusChanged += (status) =>
            {
                if (InvokeRequired) Invoke(new Action(() => this.Text = status));
                else this.Text = status;

                if (status == "Kết thúc.")
                {
                    if (InvokeRequired) Invoke(new Action(() => this.Close()));
                    else this.Close();
                }
            };
        }

        private async void btnAccept_Click(object sender, EventArgs e)
        {
            try
            {

                await _callManager.JoinCallAsync(_callId, _token);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi kết nối: " + ex.Message);
                this.Close();
            }
        }

        private void btnDecline_Click(object sender, EventArgs e)
        {
            _callManager.EndCall(); // Gửi tín hiệu bye
            this.Close();
        }

        private void InComingCall_FormClosing(object sender, FormClosingEventArgs e)
        {
            _callManager.EndCall();
        }
    }
}
