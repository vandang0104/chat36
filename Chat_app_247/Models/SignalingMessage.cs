using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Chat_app_247.Models
{
    public class SignalingMessage
    {
        public string Type { get; set; } // "offer", "answer", "candidate", "bye"
        public string Sdp { get; set; }  // Dùng cho Offer/Answer
        public string Candidate { get; set; } // Dùng cho ICE Candidate
        public string SdpMid { get; set; }
        public int SdpMLineIndex { get; set; }
        public string SenderId { get; set; }
    }
}
