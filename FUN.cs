using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    class Fun
    {
        public static void ShowEncoderError()
        {
            MessageBox.Show("外接编码器读取失败！");
        }

        /// <summary>
        /// 读取倍福模块编码器反馈
        /// </summary>
        public static bool GetAxis1EncPos(out uint uAxis1Value)
        {
            short rtn;
            uAxis1Value = 0;
            byte[] pValue = new byte[4];
            rtn = GTN.mc.GTN_GetEcatRawData(1, 82, 8, out pValue[0]);
            if (rtn != 0)
            {
                return false;
            }

            uAxis1Value = BitConverter.ToUInt32(pValue, 0);

            return true;
        }

        public static bool GetAxis2EncPos(out uint uAxis2Value)
        {
            short rtn;
            uAxis2Value = 0;
            byte[] pValue = new byte[4];
            rtn = GTN.mc.GTN_GetEcatRawData(1, 92, 8, out pValue[0]);
            if (rtn != 0)
            {
                return false;
            }
            uAxis2Value = BitConverter.ToUInt32(pValue, 0);

            return true;
        }
    }
}
