using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Forms;
using GTN;
using System.Diagnostics;
using OfficeOpenXml;
using ZedGraph;
using static OfficeOpenXml.ExcelErrorValue;
using static ZedGraph.ZedGraphControl;


namespace WindowsFormsApp1
{
    public partial class Form1 : Form
    {
        private Socket MySocket = null;
        private GraphPane Axis1Pane, Axis2Pane;
        private RollingPointPairList Axis1PosList, Axis2PosList;
        private RollingPointPairList Axis1VelList, Axis2VelList;
        private RollingPointPairList Axis1TorqueList, Axis2TorqueList;
        private static List<double> axis1PosList = new List<double>();
        private static List<double> axis1VelList = new List<double>();
        private static List<double> axis1TorqueList = new List<double>();
        private static List<double> axis2PosList = new List<double>();
        private static List<double> axis2VelList = new List<double>();
        private static List<double> axis2TorqueList = new List<double>();
        private double AxisPos1, AxisPos2;
        private double AxisVel1, AxisVel2;
        private short AxisTorque1, AxisTorque2;
        private double _lastX = 0;
        private double x;
        private double _maxX = 30; // 初始的 X 轴最大值
        private short rtn;
        private short sEcatSts;
        private short CORE = 1;
        private short Axis_X = 1;
        private short Axis_Y = 2;
        private int _npSts;

        // -------------------------------------------------------------------------------------------------------------
        // 滑模控制相关参数的定义，这里只用俯仰轴
        //private double[,] torqueMatrix = new double[2, 2]; //用于存储力矩和速度
        //private double[,] velocityMatrix = new double[2, 2];
        //private double[,] v = new double[2, 1];
        //private double[,] s = new double[2, 1];
        //private double[,] u = new double[2, 1];
        //private double[,] J = new double[2, 2];
        //private double[,] sign = new double[2, 1];
        private bool _isSlidingControlActive = false;    // 滑模控制状态标志位
        private Thread _controlThread;    // 私有线程

        private const double lambda = 5.0;        // 滑模系数 
        private const double eta = 10.0;          //    
        private const int TorqueLimit = 250;    // 力矩限制   可改
        private const int ControlPeriod = 10;    // 控制周期   可能需要更改  每8毫秒控制一次
        private const double Ixx2 = 34.3117, Ixy2 = 221.3971, Ixz2 = 105.3072, Iyz2 = -523.1959, hx2 = -8.237, hy2 = 2.0105, fv1 = 249.3782, fc1 = 49.1216, fv2 = 416.4561, fc2 = 56.3093;



        private SVGPredictor _svgp;

        // 期望轨迹参数
        private const double amplitude = 1;    // 幅值
        private const double freq = 1;    // 频率

        private double v2;
        private double s2;
        private double J2;
        private double m2;
        private double g2 = 9.8;
        // ------------------------------------------------------------------------------------------------------------

        private double encpos_x, encpos_y;
        // 实时时间戳采集
        DateTime lastPrintTime;
        private readonly Stopwatch _sw = new Stopwatch();
        private readonly List<long> _tsMs = new List<long>();
        private int _lastWrittenIndex = 0;
        private int _startIndexForThisFile = 0;
        private long _tsBaseMs = 0;
        private bool isPlaying, writePlaying;
        private uint clk;
        ExcelPackage excelPackage = new ExcelPackage();
        public static ExcelWorksheet excelWorksheet;
        double time;
        private string filePath = "data.xlsx";//数据文件路径
        private int row;
        private int count;
        double PulseEq1, PulseEq2, GearRa1, GearRa2, Round = 360, Veltime = 1000;
        private double Axis1CONVERSION_FACTOR_Pos, Axis1CONVERSION_FACTOR_Vel;//转换因子
        private double Axis2CONVERSION_FACTOR_Pos, Axis2CONVERSION_FACTOR_Vel;
        private int Axis1tableCount, Axis2tableCount;//轴1，2的数据表数量
        short Axis1pvtData, Axis2pvtData, Axis1runTable, Axis2runTable;//当前运行的数据表，下个要运行的表
        double Axis1runTime, Axis2runTime;
        bool Dataswitch = false, homestart;//数据切换标志
        //数据导入
        short[] dataAxis1;
        double[] dataPos1; double[] dataVel1; double[] dataTime1;
        short[] dataAxis2;
        double[] dataPos2; double[] dataVel2; double[] dataTime2;
        double[] absPos; double[] absVel; double[] absTime;
        ushort Axis_XpHomingStatus, Axis_YpHomingStatus;
        uint Axis1EncPos, Axis2EncPos; //读取的当前值
        uint Axis1HomeEncPos, Axis2HomeEncPos; //读回来的零位时的位置
        double Axis1HomePos, Axis2HomePos, Axis1RelativePosition, Axis2RelativePosition;//换算过的零位的位置    读取的当前值和零位值的差值
        int Axis1PositiveLimit, Axis1NegativeLimit, Axis2PositiveLimit, Axis2NegativeLimit;
        bool ReadFlag1, ReadFlag2 = true;//函数返回值
        private bool EncoderErrorFlag = false;//读取是否成功标志
        bool Axis1HomeFlag = false;
        bool Axis2HomeFlag = false;
        public Form1()
        {

            InitializeComponent();
            CreateTwoGraphPanes();
            isPlaying = false;
            writePlaying = false;

            // 显示日志框（默认在Designer里是Visible=false）
            richTextBox1.Visible = true;

        }
        //开卡
        private void Form1_Load(object sender, EventArgs e)
        {
            // 初始化 SVGPredictor
            try
            {
                string jsonPath = Path.Combine(Application.StartupPath, "gp_model_params.json");
                _svgp = new SVGPredictor(jsonPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载高斯过程模型失败: {ex.Message}\n请确保 gp_model_params.json 文件存在于程序目录下。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            excelWorksheet = excelPackage.Workbook.Worksheets.Add("数据");
            excelWorksheet.Cells[1, 4].Value = "方位角度/度";
            excelWorksheet.Cells[1, 5].Value = "方位角速度/度每秒";
            excelWorksheet.Cells[1, 6].Value = "方位力矩/Nm";
            excelWorksheet.Cells[1, 1].Value = "俯仰角度/度";
            excelWorksheet.Cells[1, 2].Value = "俯仰角速度/度每秒";
            excelWorksheet.Cells[1, 3].Value = "俯仰力矩/Nm";
            excelWorksheet.Cells[1, 7].Value = "时间/ms";

            // 确保 dataGridView1 至少有7列：0=类型, 1-3=轴1(位置/速度/力矩), 4-6=轴2(位置/速度/力矩)
            if (dataGridView1.Columns.Count < 7)
            {
                dataGridView1.Columns.Clear();
                dataGridView1.Columns.Add("colType", "类型");
                dataGridView1.Columns.Add("colA1Pos", "轴1位置");
                dataGridView1.Columns.Add("colA1Vel", "轴1速度");
                dataGridView1.Columns.Add("colA1Torque", "轴1力矩");
                dataGridView1.Columns.Add("colA2Pos", "轴2位置");
                dataGridView1.Columns.Add("colA2Vel", "轴2速度");
                dataGridView1.Columns.Add("colA2Torque", "轴2力矩");
            }

            JogcheckBox.Enabled = TrapcheckBox.Checked;
            //打开运动控制器
            rtn = GTN.mc.GTN_Open(5, 1);
            if (rtn != 0)
            {
                commandhandler("GTN_Open", rtn);
                MessageBox.Show("初始化EtherCAT总线失败，请检查相关设置。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            commandhandler("GTN_Open", rtn);
            // 初始化EtherCAT总线
            rtn = GTN.mc.GTN_InitEcatComm(CORE);
            if (rtn != 0)
            {
                commandhandler("GTN_InitEcatComm", rtn);
                MessageBox.Show("初始化EtherCAT总线失败，请检查相关设置。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // 等待EtherCAT总线初始化成功
            do
            {
                // 读取EtherCAT总线状态
                rtn = GTN.mc.GTN_IsEcatReady(CORE, out sEcatSts);
                commandhandler("GTN_IsEcatReady", rtn);
            } while (sEcatSts != 1);
            // 启动EtherCAT通讯
            rtn = GTN.mc.GTN_StartEcatComm(CORE);
            commandhandler("GTN_StartEcatComm", rtn);
            // 系统复位
            rtn = GTN.mc.GTN_Reset(CORE);
            commandhandler("GTN_Reset", rtn);
            rtn = GTN.mc.GTN_AxisOn(CORE, Axis_X);
            rtn = GTN.mc.GTN_AxisOn(CORE, Axis_Y);
            if (dataGridView2.RowCount < 2)
            {
                for (int i = 0; i <= 2; i++)
                {
                    if (dataGridView1.Rows.Count <= i) dataGridView1.Rows.Add();
                    if (dataGridView2.Rows.Count <= i) dataGridView2.Rows.Add();
                }
                this.dataGridView2.Rows[0].Cells[0].Value = 1;
                this.dataGridView2.Rows[1].Cells[0].Value = 2;
                this.dataGridView2.Rows[0].Cells[1].Value = 1000;
                this.dataGridView2.Rows[1].Cells[1].Value = 1000;
                this.dataGridView2.Rows[0].Cells[2].Value = 280;
                this.dataGridView2.Rows[1].Cells[2].Value = 280;
            }
            // 无论 dataGridView2 情况如何，都确保 dataGridView1 至少有两行并设置首列标签
            while (dataGridView1.Rows.Count < 3) dataGridView1.Rows.Add();
            this.dataGridView1.Rows[0].Cells[0].Value = "规划";
            this.dataGridView1.Rows[1].Cells[0].Value = "实际";
            this.dataGridView1.Rows[2].Cells[0].Value = "闭环反馈";
            WriteIn();
            PulseEq1 = Convert.ToDouble(this.dataGridView2.Rows[0].Cells[1].Value);
            PulseEq2 = Convert.ToDouble(this.dataGridView2.Rows[1].Cells[1].Value);
            GearRa1 = Convert.ToDouble(this.dataGridView2.Rows[0].Cells[2].Value);
            GearRa2 = Convert.ToDouble(this.dataGridView2.Rows[1].Cells[2].Value);
            Axis1CONVERSION_FACTOR_Pos = PulseEq1 * GearRa1 / Round;//脉冲当量×传动比/360度 = 终端旋转1度的脉冲数
            Axis1CONVERSION_FACTOR_Vel = Axis1CONVERSION_FACTOR_Pos / Veltime;//脉冲当量×传动比/1000ms = 终端旋转1度的脉冲数/1000ms = 终端旋转1度的脉冲速度 
            Axis2CONVERSION_FACTOR_Pos = PulseEq2 * GearRa2 / Round;
            Axis2CONVERSION_FACTOR_Vel = Axis2CONVERSION_FACTOR_Pos / Veltime;
            Axis1PositiveLimit = Convert.ToInt32(this.dataGridView2.Rows[0].Cells[3].Value) * (int)Axis1CONVERSION_FACTOR_Pos;
            Axis1NegativeLimit = Convert.ToInt32(this.dataGridView2.Rows[0].Cells[4].Value) * (int)Axis1CONVERSION_FACTOR_Pos;
            Axis2PositiveLimit = Convert.ToInt32(this.dataGridView2.Rows[1].Cells[3].Value) * (int)Axis2CONVERSION_FACTOR_Pos;
            Axis2NegativeLimit = Convert.ToInt32(this.dataGridView2.Rows[1].Cells[4].Value) * (int)Axis2CONVERSION_FACTOR_Pos;
            //rtn = GTN.mc.GTN_ZeroPos(CORE, Axis_X, 2);
            //homestart = false;
            //rtn = GTN.mc.GTN_SetSoftLimitMode(CORE, Axis_X, 0);
            //rtn = GTN.mc.GTN_SetSoftLimitMode(CORE, Axis_Y, 0);
            //// 设置软限位
            //rtn = GTN.mc.GTN_SetSoftLimit(1, Axis_X, Axis1PositiveLimit, Axis1NegativeLimit);
            //// 设置软限位
            //rtn = GTN.mc.GTN_SetSoftLimit(1, Axis_Y, Axis2PositiveLimit, Axis2NegativeLimit);


        }
        private string strPath = @".\textboxes.csv";
        private void button11_Click(object sender, EventArgs e)
        {
            StringBuilder sb = new StringBuilder();
            // 删除文件
            if (File.Exists(strPath))
                File.Delete(strPath);

            // 创建文件
            if (!File.Exists(strPath))
                File.Create(strPath).Close();
            sb.AppendLine("TextBoxName,TextBoxValue");
            sb.AppendLine($"轴1脉冲当量,{dataGridView2.Rows[0].Cells[1].Value}");
            sb.AppendLine($"轴2脉冲当量,{dataGridView2.Rows[1].Cells[1].Value}");
            sb.AppendLine($"轴1传动比,{dataGridView2.Rows[0].Cells[2].Value}");
            sb.AppendLine($"轴2传动比,{dataGridView2.Rows[1].Cells[2].Value}");
            sb.AppendLine($"轴1正限位,{dataGridView2.Rows[0].Cells[3].Value}");
            sb.AppendLine($"轴2正限位,{dataGridView2.Rows[1].Cells[3].Value}");
            sb.AppendLine($"轴1负限位,{dataGridView2.Rows[0].Cells[4].Value}");
            sb.AppendLine($"轴2负限位,{dataGridView2.Rows[1].Cells[4].Value}");
            sb.AppendLine($"ip地址,{textBox4.Text}");
            sb.AppendLine($"端口号,{textBox5.Text}");
            File.WriteAllText(strPath, sb.ToString());
        }
        private void WriteIn()//向界面写入
        {
            //判断文件是否存在
            if (!File.Exists(strPath))
                return;
            using (var reader = new StreamReader(strPath))
            {

                int lineNumber = 0;
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    var values = line.Split(',');
                    lineNumber++;
                    if (lineNumber > 1) // 跳过第一行列标题
                    {
                        switch (values[0])
                        {
                            case "轴1脉冲当量":
                                dataGridView2.Rows[0].Cells[1].Value = values[1];
                                break;
                            case "轴2脉冲当量":
                                dataGridView2.Rows[1].Cells[1].Value = values[1];
                                break;
                            case "轴1传动比":
                                dataGridView2.Rows[0].Cells[2].Value = values[1];
                                break;
                            case "轴2传动比":
                                dataGridView2.Rows[1].Cells[2].Value = values[1];
                                break;
                            case "轴1正限位":
                                dataGridView2.Rows[0].Cells[3].Value = values[1];
                                break;
                            case "轴2正限位":
                                dataGridView2.Rows[1].Cells[3].Value = values[1];
                                break;
                            case "轴1负限位":
                                dataGridView2.Rows[0].Cells[4].Value = values[1];
                                break;
                            case "轴2负限位":
                                dataGridView2.Rows[1].Cells[4].Value = values[1];
                                break;
                            case "ip地址":
                                textBox4.Text = values[1];
                                break;
                            case "端口号":
                                textBox5.Text = values[1];
                                break;
                                // 可以继续添加其他文本框的读取和赋值
                        }
                    }
                }

            }
        }

        //关卡复位
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            rtn = GTN.mc.GTN_Reset(CORE);
            rtn = GTN.mc.GTN_Close();
        }

        // 该函数检测某条GT指令的执行结果，command为指令名称，error为指令执行返回值
        // 运行时日志开关：true=成功/失败都打印；false=只打印失败（原行为）
        private bool _logAllGtnCalls = true;

        private void commandhandler(string command, short error)
        {
            // 如果 _logAllGtnCalls=false，则保持原逻辑：只在error!=0时输出
            if (!_logAllGtnCalls && error == 0) return;

            // 获取当前的本地时间
            DateTime currentTime = DateTime.Now;

            void Append()
            {
                this.richTextBox1.AppendText($"{currentTime:yyyy/M/d HH:mm:ss}: {command}={error}\n");
                this.richTextBox1.ScrollToCaret();
            }

            // 跨线程访问WinForms控件需要Invoke/BeginInvoke
            if (this.richTextBox1.InvokeRequired)
            {
                try { this.richTextBox1.BeginInvoke((Action)Append); }
                catch { /* 窗体关闭过程中可能触发 */ }
            }
            else
            {
                Append();
            }
        }
        # region 图表面板创建
        /// <summary>
        /// 创建一个图表并设置其属性
        /// </summary>
        /// <param name="graphPane">图表的 GraphPane 对象</param>
        /// <param name="title">图表的标题</param>
        /// <param name="maxX">X 轴的最大值</param>
        /// <param name="posList">位置数据列表</param>
        /// <param name="velList">速度数据列表</param>
        /// <param name="torqueList">力矩数据列表</param>
        private void CreateGraphPane(GraphPane graphPane, string title, double maxX, RollingPointPairList posList, RollingPointPairList velList, RollingPointPairList torqueList)
        {
            // 设置标题
            graphPane.Title.Text = title;
            // 设置X轴说明文字
            graphPane.XAxis.Title.Text = "时间(Ms)";
            // 设置Y轴说明文字
            graphPane.YAxis.Title.Text = "";

            graphPane.AddCurve("角度曲线（度）", posList, Color.Red, SymbolType.None);
            graphPane.AddCurve("角速度曲线（度/s）", velList, Color.Blue, SymbolType.None);
            graphPane.AddCurve("力矩曲线（Nm）", torqueList, Color.Green, SymbolType.None);

            // 设置 X 轴范围
            graphPane.XAxis.Scale.Min = 0;
            graphPane.XAxis.Scale.Max = maxX;
            graphPane.XAxis.Scale.MinorStep = 10;
            graphPane.XAxis.Scale.MajorStep = 100;
        }
        /// <summary>
        /// 创建两个图表面板
        /// </summary>
        private void CreateTwoGraphPanes()
        {
            Axis1Pane = zedGraphControl1.GraphPane;
            Axis1PosList = new RollingPointPairList(2400);
            Axis1VelList = new RollingPointPairList(2400);
            Axis1TorqueList = new RollingPointPairList(2400);
            CreateGraphPane(Axis1Pane, "方位实时曲线", _maxX, Axis1PosList, Axis1VelList, Axis1TorqueList);

            Axis2Pane = zedGraphControl3.GraphPane;
            Axis2PosList = new RollingPointPairList(2400);
            Axis2VelList = new RollingPointPairList(2400);
            Axis2TorqueList = new RollingPointPairList(2400);
            CreateGraphPane(Axis2Pane, "俯仰实时曲线", _maxX, Axis2PosList, Axis2VelList, Axis2TorqueList);
        }
        #endregion
        #region 曲线数据点数值显示
        /// <summary>
        /// 显示鼠标位置对应的曲线点的数值
        /// </summary>
        /// <param name="graphPane">图表的 GraphPane 对象</param>
        /// <param name="label">显示数值的标签控件</param>
        /// <param name="mousePos">鼠标位置的坐标</param>  
        private void UpdateLabelWithNearestPoint(GraphPane graphPane, System.Windows.Forms.Label label, PointF mousePos)
        {
            // 获取鼠标位置对应的曲线点
            CurveItem nearestCurve;
            int nearestIndex;
            double x, y;
            if (graphPane.FindNearestPoint(mousePos, out nearestCurve, out nearestIndex))
            {
                // 获取点的X和Y值
                PointPair point = nearestCurve[nearestIndex];
                x = point.X;
                y = point.Y;

                // 显示数值（例如在标签中）
                label.Text = $"X: {x}, Y: {y}";
            }
            else
            {
                // 如果没有找到最近的点，清除显示
                label.Text = "";
            }
        }
        private void zedGraphControl1_MouseMove(object sender, MouseEventArgs e)
        {

            // 获取鼠标位置的坐标
            PointF mousePos = new PointF(e.X, e.Y);
            UpdateLabelWithNearestPoint(Axis1Pane, label1, mousePos);
        }
        private void zedGraphControl2_MouseMove(object sender, MouseEventArgs e)
        {
            // 获取鼠标位置的坐标
            PointF mousePos = new PointF(e.X, e.Y);
            UpdateLabelWithNearestPoint(Axis2Pane, label6, mousePos);
        }
        #endregion
        # region 图标曲线可见性设置
        /// <summary>
        /// 遍历图表中的曲线，根据曲线名称设置其可见性
        /// </summary>
        /// <param name="graphPane">图表的 GraphPane 对象</param>
        /// <param name="curveName">曲线名称</param>
        /// <param name="isVisible">是否可见</param>
        private void SetCurveVisibilityByName(GraphPane graphPane, string curveName, bool isVisible)
        {
            CurveList curveList = graphPane.CurveList;
            foreach (CurveItem curve in curveList)
            {
                if (curve.Label.Text == curveName)
                {
                    curve.IsVisible = isVisible;
                }
            }
        }
        /// <summary>
        /// 根据指定的复选框的状态设置图表中的曲线的可见性
        /// </summary>
        private void CheckBox_CheckedChanged(GraphPane graphPane, CheckBox checkBox, string curveName)
        {
            if (checkBox.Checked)
            {
                SetCurveVisibilityByName(graphPane, curveName, true); // 显示曲线
            }
            else
            {
                SetCurveVisibilityByName(graphPane, curveName, false); // 隐藏曲线
            }
        }
        private void Axis1PoscheckBox_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox_CheckedChanged(Axis1Pane, Axis1PoscheckBox, "位置曲线");
        }
        private void Axis1VelcheckBox_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox_CheckedChanged(Axis1Pane, Axis1VelcheckBox, "速度曲线");
        }
        private void Axis1TorquecheckBox_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox_CheckedChanged(Axis1Pane, Axis1TorquecheckBox, "力矩曲线");
        }
        private void Axis2PoscheckBox_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox_CheckedChanged(Axis2Pane, Axis2PoscheckBox, "位置曲线");
        }
        private void Axis2VelcheckBox_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox_CheckedChanged(Axis2Pane, Axis2VelcheckBox, "速度曲线");
        }
        private void Axis2TorquecheckBox_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox_CheckedChanged(Axis2Pane, Axis2TorquecheckBox, "力矩曲线");
        }
        # endregion
        #region 轴状态显示
        /// <summary>
        /// 根据指定的位掩码更新标签的颜色
        /// </summary>
        private void UpdateLabelColor(System.Windows.Forms.Label label, int value, uint mask, Color trueColor, Color falseColor)
        {
            if ((value & mask) == 0)
            {
                label.BackColor = trueColor;
            }
            else
            {
                label.BackColor = falseColor;
            }
        }

        /// <summary>
        /// 读取轴的状态并更新标签的颜色
        /// </summary>
        private void AxisStata(short Axis, System.Windows.Forms.Label servoEnableLabel, System.Windows.Forms.Label servoAlarmLabel, System.Windows.Forms.Label positiveLimitLabel, System.Windows.Forms.Label negativeLimitLabel)//轴状态读取
        {
            int StsValue = 0;
            rtn = mc.GT_GetSts(Axis, out StsValue, 1, out clk);
            //伺服使能触发
            UpdateLabelColor(servoEnableLabel, StsValue, 0x200, Color.LimeGreen, Color.Red);
            //伺服报警触发
            UpdateLabelColor(servoAlarmLabel, StsValue, 0x2, Color.LimeGreen, Color.Red);
            //正限位触发
            UpdateLabelColor(positiveLimitLabel, StsValue, 0x20, Color.LimeGreen, Color.Red);
            //负限位触发
            UpdateLabelColor(negativeLimitLabel, StsValue, 0x40, Color.LimeGreen, Color.Red);
        }
        Dictionary<int, int[]> cellIndices = new Dictionary<int, int[]>
            {
                { 1, new int[] { 1, 2, 3 } },
                { 2, new int[] { 4, 5, 6 } }
            };
        private void timer2_Tick(object sender, EventArgs e)
        {
            if (homestart)
            {
                rtn = GTN.mc.GTN_GetEcatHomingStatus(CORE, Axis_X, out Axis_XpHomingStatus);
                rtn = GTN.mc.GTN_GetEcatHomingStatus(CORE, Axis_Y, out Axis_YpHomingStatus);
                if (Axis_XpHomingStatus == 3)
                {
                    rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_X, 8);
                    MessageBox.Show("俯仰回零成功", "提示");
                    Axis1HomeFlag = true;
                    //rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_Y, 8);
                }
                if (Axis_YpHomingStatus == 3)
                {
                    //rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_X, 8);
                    rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_Y, 8);
                    MessageBox.Show("方位回零成功", "提示");
                    Axis2HomeFlag = true;
                }
                if (Axis1HomeFlag && Axis2HomeFlag)
                {
                    rtn = GTN.mc.GTN_ZeroPos(CORE, Axis_X, 2);
                    homestart = false;
                    Thread.Sleep(500);
                    ReadFlag1 = Fun.GetAxis1EncPos(out Axis1HomeEncPos);//回完零后读取编码器位置当做0
                    ReadFlag2 = Fun.GetAxis2EncPos(out Axis2HomeEncPos);
                    if (!ReadFlag2 || !ReadFlag1)
                    {
                        if (!EncoderErrorFlag)
                        {
                            EncoderErrorFlag = true;
                            Fun.ShowEncoderError();
                        }
                        return;
                    }
                    EncoderErrorFlag = false;
                    Axis1HomePos = Axis1HomeEncPos/ 2.996743 / Axis1CONVERSION_FACTOR_Pos;
                    Axis2HomePos = Axis2HomeEncPos /2.811797 / Axis2CONVERSION_FACTOR_Pos;
                    rtn = GTN.mc.GTN_SetSoftLimitMode(CORE, Axis_X, 0);
                    rtn = GTN.mc.GTN_SetSoftLimitMode(CORE, Axis_Y, 0);
                    // 设置软限位
                    rtn = GTN.mc.GTN_SetSoftLimit(1, Axis_X, Axis1PositiveLimit, Axis1NegativeLimit);
                    // 设置软限位
                    rtn = GTN.mc.GTN_SetSoftLimit(1, Axis_Y, Axis2PositiveLimit, Axis2NegativeLimit);
                    Axis1HomeFlag = false;
                    Axis2HomeFlag = false;

                }
            }

            // 读取轴1闭环反馈位置
            ReadFlag1 = Fun.GetAxis1EncPos(out Axis1EncPos);
            textBox6.Text = Axis1EncPos.ToString();
            // 读取轴2闭环反馈位置
            ReadFlag2 = Fun.GetAxis2EncPos(out Axis2EncPos);
            textBox7.Text = Axis2EncPos.ToString();

            if (!ReadFlag2 || !ReadFlag1)
            {
                if (!EncoderErrorFlag)
                {
                    EncoderErrorFlag = true;
                    Fun.ShowEncoderError();
                }
                return;
            }
            EncoderErrorFlag = false;
            Axis1RelativePosition = Math.Round(Axis1EncPos/ 2.996743 / Axis1CONVERSION_FACTOR_Pos - Axis1HomePos, 3);//实时读的值减去0位读的值就是位移量；
            Axis2RelativePosition = Math.Round(Axis2EncPos /2.811797 / Axis2CONVERSION_FACTOR_Pos - Axis2HomePos, 3);

            // 读取轴1的状态
            AxisStata(1, Axis1ServoEnableLabel, Axis1ServoAlarmLabel, Axis1PositiveLimitLabel, Axis1NegativeLimitLabel);
            // 读取轴2的状态
            AxisStata(2, Axis2ServoEnableLabel, Axis2ServoAlarmLabel, Axis2PositiveLimitLabel, Axis2NegativeLimitLabel);

            // 防御：确保 dataGridView1 至少有2行且>=7列，否则先不写入，避免索引越界
            if (dataGridView1.Rows.Count < 3 || dataGridView1.Columns.Count < 7)
            {
                return;
            }

            for (int i = 1; i <= 2; i++)
            {
                double axisPos, axisVel, axisPrfPos, axisPrfVel;
                short axisTorque;
                UpdateAxisData((short)i, out axisPos, out axisVel, out axisTorque, out axisPrfPos, out axisPrfVel);
                int[] indices = cellIndices[i];
                this.dataGridView1.Rows[0].Cells[indices[0]].Value = axisPrfPos;
                this.dataGridView1.Rows[0].Cells[indices[1]].Value = axisPrfVel;
                this.dataGridView1.Rows[1].Cells[indices[0]].Value = axisPos;
                this.dataGridView1.Rows[1].Cells[indices[1]].Value = axisVel;
                this.dataGridView1.Rows[1].Cells[indices[2]].Value = axisTorque;
                if (i == 1)
                {
                    this.dataGridView1.Rows[2].Cells[indices[0]].Value = Axis1RelativePosition;
                }
                else if (i == 2)
                {
                    this.dataGridView1.Rows[2].Cells[indices[0]].Value = Axis2RelativePosition;
                }
            }
            if (Dataswitch)
            {
                ProcessAxisAsync(CORE, Axis_X, ref Axis1runTable, Axis1tableCount);
                ProcessAxisAsync(CORE, Axis_Y, ref Axis2runTable, (Axis1tableCount + Axis2tableCount));
            }
            if (checkBox1.Checked == true)
            {
                button8.Enabled = false;
                button9.Enabled = false;
            }
            else
            {
                button8.Enabled = true;
                button9.Enabled = true;
            }


        }
        private void ProcessAxisAsync(short CORE, short axis, ref short runTable, int tableCount)
        {
            short pvtData;
            double runTime;
            rtn = GTN.mc.GTN_PvtStatus(CORE, axis, out pvtData, out runTime, 1);
            if (runTable < tableCount)
            {
                // 检查PVT数据
                if (runTable == pvtData)
                {
                    // 更新表号并选择新表
                    runTable++;
                    rtn = GTN.mc.GTN_PvtTableSelect(CORE, axis, runTable);
                }

            }

        }
        #endregion
        #region 图表刷新以及文件写入
        // 轴位置、速度、力矩数据读取
        private void UpdateAxisData(short axisIndex, out double axisPos, out double axisVel, out short axisTorque, out double axisPrfPos, out double axisPrfVel)
        {
            double[] posConversionFactors = { 0, Axis1CONVERSION_FACTOR_Pos, Axis2CONVERSION_FACTOR_Pos };
            double[] velConversionFactors = { 0, Axis1CONVERSION_FACTOR_Vel, Axis2CONVERSION_FACTOR_Vel };
            double rawAxisPos, rawAxisVel, rawAxisPrfPos, rawAxisPrfVel;
            rtn = mc.GTN_GetEncPos(CORE, axisIndex, out rawAxisPos, 1, out clk);
            axisPos = rawAxisPos / posConversionFactors[axisIndex];
            rtn = mc.GTN_GetEncVel(CORE, axisIndex, out rawAxisVel, 1, out clk);
            axisVel = rawAxisVel / velConversionFactors[axisIndex];
            rtn = mc.GTN_GetPrfPos(CORE, axisIndex, out rawAxisPrfPos, 1, out clk);
            axisPrfPos = rawAxisPrfPos / posConversionFactors[axisIndex];
            rtn = mc.GTN_GetPrfVel(CORE, axisIndex, out rawAxisPrfVel, 1, out clk);
            axisPrfVel = rawAxisPrfVel / velConversionFactors[axisIndex];
            rtn = mc.GTN_GetEcatAxisAtlTorque(CORE, axisIndex, out axisTorque);
        }
        //图表刷新以及文件写入
        private void timer1_Tick(object sender, EventArgs e)
        {
            double x = _lastX + 1; // X 轴使用时间作为示例

            // 原子性数据采集和时间戳记录
            long timestamp = 0;
            if (writePlaying)
            {
                if (!_sw.IsRunning) _sw.Start();
                timestamp = _sw.ElapsedMilliseconds - _tsBaseMs;
            }

            // 将新的数据点添加到 RollingPointPairList 中
            try
            {
                for (int i = 1; i <= 2; i++)
                {
                    double axisPos, axisVel, axisPrfPos, axisPrfVel;
                    short axisTorque;
                    UpdateAxisData((short)i, out axisPos, out axisVel, out axisTorque, out axisPrfPos, out axisPrfVel);

                    switch (i)
                    {
                        case 1:
                            Axis1PosList.Add(x, axisPos);
                            Axis1VelList.Add(x, axisVel);
                            Axis1TorqueList.Add(x, axisTorque);
                            if (writePlaying)
                            {
                                axis1PosList.Add(axisPos);
                                axis1VelList.Add(axisVel);
                                axis1TorqueList.Add((double)axisTorque);
                            }

                            break;
                        case 2:
                            Axis2PosList.Add(x, axisPos);
                            Axis2VelList.Add(x, axisVel);
                            Axis2TorqueList.Add(x, axisTorque);
                            if (writePlaying)
                            {
                                axis2PosList.Add(axisPos);
                                axis2VelList.Add(axisVel);
                                axis2TorqueList.Add((double)axisTorque);
                            }
                            break;
                    }
                }

                // 在所有数据成功添加后，立即记录时间戳
                if (writePlaying)
                {
                    _tsMs.Add(timestamp);
                }
            }
            catch (Exception ex)
            {
                // 发生异常时停止数据记录，确保数据一致性
                writePlaying = false;

                // 清理可能不完整的数据，保持数据同步
                if (axis1PosList.Count > _tsMs.Count)
                    axis1PosList.RemoveAt(axis1PosList.Count - 1);
                if (axis1VelList.Count > _tsMs.Count)
                    axis1VelList.RemoveAt(axis1VelList.Count - 1);
                if (axis1TorqueList.Count > _tsMs.Count)
                    axis1TorqueList.RemoveAt(axis1TorqueList.Count - 1);
                if (axis2PosList.Count > _tsMs.Count)
                    axis2PosList.RemoveAt(axis2PosList.Count - 1);
                if (axis2VelList.Count > _tsMs.Count)
                    axis2VelList.RemoveAt(axis2VelList.Count - 1);
                if (axis2TorqueList.Count > _tsMs.Count)
                    axis2TorqueList.RemoveAt(axis2TorqueList.Count - 1);

                // 可以选择记录错误日志
                MessageBox.Show($"数据采集异常，已停止记录：{ex.Message}", "数据错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            // 更新 X 轴范围
            Scale xScale = Axis1Pane.XAxis.Scale;
            Scale yScale = Axis2Pane.XAxis.Scale;
            if (x > xScale.Max - xScale.MajorStep)
            {
                double newMaxX = x + xScale.MajorStep;
                double newMinX = newMaxX - 300.0; // 保持显示最新的30个单位时间的数据

                // 更新 Axis2Pane 的 X 轴范围
                xScale.Max = newMaxX;
                xScale.Min = newMinX;

                // 更新 Axis1Pane 的 X 轴范围
                yScale.Max = newMaxX;
                yScale.Min = newMinX;

            }

            // 更新 lastX
            _lastX = x;

            // 刷新图表
            zedGraphControl1.AxisChange();
            zedGraphControl1.Invalidate();
            zedGraphControl3.AxisChange();
            zedGraphControl3.Invalidate();
        }
        #endregion
        private void button1_Click(object sender, EventArgs e)
        {
            if (isPlaying)
            {
                timer1.Stop();
                button1.BackgroundImage = Properties.Resources.播放1;

            }
            else
            {
                _lastX = 0;
                _maxX = 300;
                Axis1PosList.Clear();
                Axis1VelList.Clear();
                Axis1TorqueList.Clear();
                Axis2PosList.Clear();
                Axis2VelList.Clear();
                Axis2TorqueList.Clear();
                // 设置 X 轴范围
                Axis1Pane.XAxis.Scale.Min = 0;
                Axis1Pane.XAxis.Scale.Max = _maxX;
                Axis1Pane.XAxis.Scale.MinorStep = 10;
                Axis1Pane.XAxis.Scale.MajorStep = 100;
                Axis2Pane.XAxis.Scale.Min = 0;
                Axis2Pane.XAxis.Scale.Max = _maxX;
                Axis2Pane.XAxis.Scale.MinorStep = 10;
                Axis2Pane.XAxis.Scale.MajorStep = 100;
                // 更新图表显示
                zedGraphControl1.Invalidate();
                //timer1.Interval = Convert.ToInt32(textBox1.Text);
                timer1.Interval = 1;
                timer1.Start();
                button1.BackgroundImage = Properties.Resources.暂停1;


            }
            isPlaying = !isPlaying;

        }
        private void button2_Click(object sender, EventArgs e)
        {
            // 设置X轴和Y轴的范围为自动计算的值
            Axis1Pane.XAxis.Scale.MinAuto = true;
            Axis1Pane.XAxis.Scale.MaxAuto = true;
            Axis1Pane.YAxis.Scale.MinAuto = true;
            Axis1Pane.YAxis.Scale.MaxAuto = true;
            // 更新控件
            zedGraphControl1.AxisChange();
            zedGraphControl1.Invalidate();
        }

        private void button12_Click(object sender, EventArgs e)
        {
            rtn = GTN.mc.GTN_ClrSts(CORE, 1, 2);
        }

        private void button15_Click(object sender, EventArgs e)
        {
            rtn = GTN.mc.GTN_Stop(CORE, 1, 1);
        }

        private void button21_Click(object sender, EventArgs e)
        {
            rtn = GTN.mc.GTN_SetEcatAxisMode(CORE, Axis_X, 10); // 俯仰：力矩控制模式
            commandhandler("GTN_SetEcatAxisMode Axis_X=10", rtn);
        }

        private void button22_Click(object sender, EventArgs e)
        {
            rtn = GTN.mc.GTN_SetEcatAxisPT(CORE, Axis_X, 50);  // 俯仰
            commandhandler("GTN_SetEcatAxisPT Axis_X=10", rtn);
        }

        private void button16_Click(object sender, EventArgs e)
        {
            rtn = GTN.mc.GTN_Stop(CORE, 2, 1);
        }

        private void button14_Click(object sender, EventArgs e)
        {
            int StsValueK = 0;
            rtn = mc.GT_GetSts(Axis_X, out StsValueK, 1, out clk);
            if ((StsValueK & 0x200) != 0)
            {
                rtn = GTN.mc.GTN_AxisOff(CORE, Axis_X);
                button14.Text = "俯仰使能开启";
            }
            else
            {
                rtn = GTN.mc.GTN_AxisOn(CORE, Axis_X);
                button14.Text = "俯仰使能关闭";
            }
        }

        private void button23_Click(object sender, EventArgs e)
        {
            double x = Axis1EncPos / Axis1CONVERSION_FACTOR_Pos;
            textBox8.Text = x.ToString();
            double y = Axis1EncPos / Axis2CONVERSION_FACTOR_Pos;
            textBox9.Text = y.ToString();
        }

        private void button17_Click(object sender, EventArgs e)
        {
            int StsValueK = 0;
            rtn = mc.GT_GetSts(Axis_Y, out StsValueK, 1, out clk);
            if ((StsValueK & 0x200) != 0)
            {
                rtn = GTN.mc.GTN_AxisOff(CORE, Axis_Y);
                button17.Text = "方位使能开启";
            }
            else
            {
                rtn = GTN.mc.GTN_AxisOn(CORE, Axis_Y);
                button17.Text = "方位使能关闭";
            }
        }

        private void button13_Click(object sender, EventArgs e)
        {
            rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_X, 6);
            rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_Y, 6);
            // 设置回零参数
            rtn = GTN.mc.GTN_SetEcatHomingPrm(1, Axis_X, 22, 1000, 3000, 1000, 0, 0);
            // 设置回零参数
            rtn = GTN.mc.GTN_SetEcatHomingPrm(1, Axis_Y, 22, 1000, 3000, 1000, 0, 0);
            rtn = GTN.mc.GTN_StartEcatHoming(CORE, Axis_X);
            rtn = GTN.mc.GTN_StartEcatHoming(CORE, Axis_Y);
            homestart = true;
            //while ((Axis_XpHomingStatus & 1) ==1&& (Axis_YpHomingStatus & 1) == 1)
            //{
            //    rtn = GTN.mc.GTN_GetEcatHomingStatus(CORE, Axis_X, out Axis_XpHomingStatus);
            //    rtn = GTN.mc.GTN_GetEcatHomingStatus(CORE, Axis_X, out Axis_YpHomingStatus);
            //}
            //rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_X, 8);
            //rtn = GTN.mc.GTN_SetHomingMode(CORE, Axis_Y, 8);
        }
        #region 点动||寸动选择判断
        private void JogcheckBox_CheckedChanged(object sender, EventArgs e)
        {
            TrapcheckBox.Enabled = JogcheckBox.Checked;
            if (JogcheckBox.Checked)
            {
                TrapcheckBox.Checked = false;
            }
        }
        // -----------------------------------------------------------------------------------------------------------------
        // 力矩控制按钮
        private void smc_Click(object sender, EventArgs e)
        {
            if (!_isSlidingControlActive)
            {
                _isSlidingControlActive = true;
                smc.Text = "启动控制";
                // 启动控制线程
                _controlThread = new Thread(SlidingModeControlLoop);
                _controlThread.Priority = ThreadPriority.Highest;
                _controlThread.Start();
            }
            else
            {
                _isSlidingControlActive = false;
                smc.Text = "停止控制";
                // 恢复位置控制模式
                GTN.mc.GTN_SetEcatAxisMode(CORE, Axis_X, 8); // CSP模式
            }

        }

        // 滑模控制算法实现
        // 1.输入轨迹  轨迹×减速比    Axis_X俯仰轴   Axis_Y方位轴
        // 2.控制周期
        // 3.数据采集 等间隔采样

        private void SlidingModeControlLoop()
        {
            try
            {
                // --- 验证：切换到力矩模式（CST/力矩控制）是否成功 ---
                rtn = GTN.mc.GTN_SetEcatAxisMode(CORE, Axis_X, 10); // 俯仰：力矩控制模式
                commandhandler("GTN_SetEcatAxisMode Axis_X=10", rtn);
                rtn = GTN.mc.GTN_SetEcatAxisMode(CORE, Axis_Y, 10); // 方位：力矩控制模式
                commandhandler("GTN_SetEcatAxisMode Axis_Y=10", rtn);

                // 若模式切换失败，直接退出控制线程（避免后续PT一直失败刷屏）
                if (rtn != 0)
                {
                    commandhandler("滑模控制：切换力矩模式失败，已退出控制线程", 1);
                    _isSlidingControlActive = false;
                    return;
                }

                double currentPosX, currentVelX, currentAccX, currentPosY, currentVelY, currentAccY, currentEncPosX, currentEncVelX, currentEncPosY, currentEncVelY;
                double amplitude = 40;
                double frequency = 0.02;  // 正弦波频率（Hz）
                double time = 0.0;       //时间变量，用于计算随时间变化的期望轨迹。通常会在控制周期中不断累加（如每过 ControlPeriod 时间，time 增加对应的值），作为正弦函数的输入参数。
                double k = 30;           //切换增益
                double L1 = 1e-10;

                // 使用“实际测得”的控制回路间隔 dt（秒），而不是固定ControlPeriod
                // 方案A：使用参考加速度 qdd_ref（不再需要速度滤波与差分估计加速度）
                double dt = ControlPeriod / 1000.0; // 兜底值：首次循环还没有实际dt

                // 更稳的10ms节拍：用Stopwatch做绝对时间调度，避免Thread.Sleep累计漂移
                var sw = Stopwatch.StartNew();
                long nextTickMs = sw.ElapsedMilliseconds; // 下一次循环的目标时刻（ms）

                // 用于计算“实际dt”
                long lastLoopMs = sw.ElapsedMilliseconds;

                // --- 验证日志节流：避免每10ms刷屏 ---
                int loopCounter = 0;
                int logEveryN = Math.Max(1, 1000 / ControlPeriod); // 约每1秒输出一次（ControlPeriod=10ms => 100次）

                while (_isSlidingControlActive)
                {
                    loopCounter++;
                    // 实际dt（秒）：基于Stopwatch测量本次循环距上次循环的时间
                    long nowLoopMs = sw.ElapsedMilliseconds;
                    dt = Math.Max(1e-6, (nowLoopMs - lastLoopMs) / 1000.0);
                    lastLoopMs = nowLoopMs;
                    // 使用实际时间推进轨迹时间，避免sleep抖动/超时导致轨迹时间不准
                    time += dt;

                    double desiredPosX = amplitude * Math.Sin(2 * Math.PI * frequency * time) * Math.PI / 180.0; //*减速比;
                    double desiredVelX = amplitude * 2 * Math.PI * frequency * Math.Cos(2 * Math.PI * frequency * time) * Math.PI / 180.0;//*减速比
                    double desiredAccX = -amplitude * 4 * Math.PI * Math.PI * frequency * frequency * Math.Sin(2 * Math.PI * frequency * time) * Math.PI / 180.0;//*减速比
                    double desiredPosY = amplitude * Math.Sin(2 * Math.PI * frequency * time) * Math.PI / 180.0; //*减速比;
                    double desiredVelY = amplitude * 2 * Math.PI * frequency * Math.Cos(2 * Math.PI * frequency * time) * Math.PI / 180.0;//*减速比
                    double desiredAccY = -amplitude * 4 * Math.PI * Math.PI * frequency * frequency * Math.Sin(2 * Math.PI * frequency * time) * Math.PI / 180.0;//*减速比

                    // 1. 获取当前状态（500us读取周期）
                    GTN.mc.GTN_GetEncPos(CORE, Axis_X, out currentEncPosX, 1, out _);//俯仰,获取当前脉冲
                    GTN.mc.GTN_GetEncVel(CORE, Axis_X, out currentEncVelX, 1, out _);
                    GTN.mc.GTN_GetEncPos(CORE, Axis_Y, out currentEncPosY, 1, out _);//方位
                    GTN.mc.GTN_GetEncVel(CORE, Axis_Y, out currentEncVelY, 1, out _);

                    // 2. 脉冲-天线转动角度
                    currentPosX = currentEncPosX / Axis1CONVERSION_FACTOR_Pos * Math.PI / 180.0;
                    currentVelX = currentEncVelX / Axis1CONVERSION_FACTOR_Vel * Math.PI / 180.0;
                    currentPosY = currentEncPosY / Axis2CONVERSION_FACTOR_Pos * Math.PI / 180.0;
                    currentVelY = currentEncVelY / Axis2CONVERSION_FACTOR_Vel * Math.PI / 180.0;

                    // 2.1 方案A：使用参考加速度，不再差分估计实际加速度

                    // 3. 计算跟踪误差,参考速度,参考加速度
                    double posErrorX = desiredPosX - currentPosX;
                    double velErrorX = desiredVelX - currentVelX;
                    double posErrorY = desiredPosY - currentPosY;
                    double velErrorY = desiredVelY - currentVelY;


                    // 4. 构建滑模面
                    //double slidingSurfaceX = currentVelX - ddqrefX;
                    //double slidingSurfaceY = currentVelY - ddqrefY;
                    double slidingSurfaceX = currentVelX - desiredVelX + lambda * (currentPosX - desiredPosX);
                    double slidingSurfaceY = currentVelY - desiredVelY + lambda * (currentPosY - desiredPosY);
                    //double absSX = Math.Abs(slidingSurfaceX);
                    //double absSY = Math.Abs(slidingSurfaceX);

                    // 5. 构建MCGF矩阵
                    double[,] M = new double[2, 2];
                    M[0, 0] = Ixx2 * Math.Pow(Math.Sin(currentPosX), 2)
                           + 2 * Ixy2 * Math.Sin(currentPosX) * Math.Cos(currentPosX)
                           + 2 * L1 * hx2 * Math.Cos(currentPosX)
                           - 2 * L1 * hy2 * Math.Sin(currentPosX);
                    M[0, 1] = Ixz2 * Math.Sin(currentPosX) + Iyz2 * Math.Cos(currentPosX);
                    M[1, 0] = M[0, 1];
                    M[1, 1] = 0;

                    double[,] C = new double[2, 2];
                    C[0, 0] = 0;
                    C[0, 1] = 2 * (Ixx2 * Math.Sin(currentPosX) * Math.Cos(currentPosX)
                               + Ixy2 * (Math.Cos(currentPosX) * Math.Cos(currentPosX)
                               - Math.Sin(currentPosX) * Math.Sin(currentPosX))
                               - L1 * hx2 * Math.Sin(currentPosX)
                               - L1 * hy2 * Math.Cos(currentPosX)) * currentPosY
                            + (Ixz2 * Math.Cos(currentPosX) - Iyz2 * Math.Sin(currentPosX)) * currentVelX;
                    C[1, 0] = (-Ixx2 * Math.Sin(currentPosX) * Math.Cos(currentPosX)
                              + (Math.Sin(currentPosX) * Math.Sin(currentPosX)
                              - Math.Cos(currentPosX) * Math.Cos(currentPosX)) * Ixy2
                              + L1 * hx2 * Math.Sin(currentPosX)
                              + L1 * hy2 * Math.Cos(currentPosX)) * currentPosY;
                    C[1, 1] = 0;

                    double[] F = new double[2];
                    F[0] = fv1 * currentPosY + fc1 * Math.Sign(currentPosY);
                    F[1] = fv2 * currentVelX + fc2 * Math.Sign(currentVelX);

                    double[] g = new double[2];
                    g[0] = 0;
                    g[1] = g2 * Math.Cos(currentPosX) * hx2 - g2 * Math.Sin(currentPosX) * hy2;

                    // 6. 导出高斯过程估计值
                    // 数据格式    
                    // 改
                    // 构造与模型输入维度一致的特征（此处模型为6维，若不一致可按需扩展/裁剪）
                    //var feats = new float[6];
                    //feats[0] = (float)currentPosX;
                    //feats[1] = (float)currentVelX;
                    //// 其余特征暂以0占位，建议替换为实际可用的输入特征
                    //float[][] input = new float[1][] { feats };

                    //double gpMean = 0.0, gpVar = 0.0;
                    //if (_svgp != null)
                    //{
                    //    var (mean, var) = _svgp.Predict(input);  // 调用SVGPredictor预测
                    //    // 检查返回结果的维度，防止索引越界
                    //    if (mean != null && mean.Length > 0 && mean[0] != null && mean[0].Length > 1)
                    //    {
                    //        gpMean = mean[0][1];  // 使用第二个潜变量输出
                    //    }
                    //    if (var != null && var.Length > 0 && var[0] != null && var[0].Length > 1)
                    //    {
                    //        gpVar = var[0][1];
                    //    }
                    //}

                    // 7. 构建边界层函数
                    //double phi = 0.1 + gpMean;
                    //double satArg = slidingSurfaceX / Math.Max(phi, 1e-6);
                    //double sat = Math.Max(-1.0, Math.Min(1.0, satArg));

                    // 8. 2DOF 向量滑模控制：tau = M(q)*qdd_ref + C(q,qd)*qd + g(q) + F(qd) - K*sat(s/phi)
                    // 方案A：使用参考加速度（不对速度做滤波；也不差分估计实际加速度）
                    // 与 s = (qd-qd_d) + lambda*(q-q_d) 匹配的常用选择：qdd_ref = qdd_d - lambda*(qd - qd_d)
                    double qddRefX = desiredAccX - lambda * (currentVelX - desiredVelX);
                    double qddRefY = desiredAccY - lambda * (currentVelY - desiredVelY);

                    // 等效项：M*qdd_ref + C*qd + g + F
                    double tauEqX = M[0, 0] * qddRefX + M[0, 1] * qddRefY
                                 + C[0, 0] * currentVelX + C[0, 1] * currentVelY
                                 + g[0] + F[0];
                    double tauEqY = M[1, 0] * qddRefX + M[1, 1] * qddRefY
                                 + C[1, 0] * currentVelX + C[1, 1] * currentVelY
                                 + g[1] + F[1];

                    // 切换项：sat(s/phi) 代替 sgn(s)，降低抖振
                    const double phi = 0.05; // 边界层厚度(单位与s一致)，可调：越大越平滑但稳态误差可能增大
                    double satX = Sat(slidingSurfaceX / Math.Max(phi, 1e-9));
                    double satY = Sat(slidingSurfaceY / Math.Max(phi, 1e-9));

                    double tauSwX = -k * satX;
                    double tauSwY = -k * satY;

                    double torqueX = tauEqX + tauSwX;
                    double torqueY = tauEqY + tauSwY;

                    // 9. 力矩限幅（分别限幅）
                    short torqueOutputX = (short)Clamp(torqueX, -TorqueLimit, TorqueLimit);
                    short torqueOutputY = (short)Clamp(torqueY, -TorqueLimit, TorqueLimit);

                    // 10. 输出力矩（并验证下发是否成功）
                    rtn = GTN.mc.GTN_SetEcatAxisPT(CORE, Axis_X, torqueOutputX);  // 俯仰
                    if ((loopCounter % logEveryN) == 0)
                    {
                        commandhandler($"GTN_SetEcatAxisPT Axis_X cmd={torqueOutputX}", rtn);
                        // 当PT下发失败时，读取并打印轴状态字（十六进制）
                        if (rtn != 0)
                        {
                            int stsX = 0, stsY = 0;
                            short r1 = mc.GT_GetSts(Axis_X, out stsX, 1, out clk);
                            short r2 = mc.GT_GetSts(Axis_Y, out stsY, 1, out clk);
                            commandhandler($"GT_GetSts Axis_X=0x{stsX:X8} (rtn={r1}), Axis_Y=0x{stsY:X8} (rtn={r2})", 0);
                        }
                    }

                    rtn = GTN.mc.GTN_SetEcatAxisPT(CORE, Axis_Y, torqueOutputY);  // 方位
                    if ((loopCounter % logEveryN) == 0)
                    {
                        commandhandler($"GTN_SetEcatAxisPT Axis_Y cmd={torqueOutputY}", rtn);
                        // 当PT下发失败时，读取并打印轴状态字（十六进制）
                        if (rtn != 0)
                        {
                            int stsX = 0, stsY = 0;
                            short r1 = mc.GT_GetSts(Axis_X, out stsX, 1, out clk);
                            short r2 = mc.GT_GetSts(Axis_Y, out stsY, 1, out clk);
                            commandhandler($"GT_GetSts Axis_X=0x{stsX:X8} (rtn={r1}), Axis_Y=0x{stsY:X8} (rtn={r2})", 0);
                        }
                    }

                    // 11. 维持控制周期（更稳的节拍：基于Stopwatch的绝对时间调度，减少Thread.Sleep抖动带来的漂移）
                    nextTickMs += ControlPeriod;

                    long nowMs = sw.ElapsedMilliseconds;
                    int sleepMs = (int)(nextTickMs - nowMs);
                    if (sleepMs > 0)
                    {
                        Thread.Sleep(sleepMs);
                    }
                    else
                    {
                        // 已经超时：不sleep，直接进入下一轮（可根据需要统计超时次数）
                    }
                }
            }
            catch (Exception ex)
            {
                BeginInvoke((Action)(() =>
                {
                    MessageBox.Show($"控制线程异常：{ex.Message}");
                    _isSlidingControlActive = false;

                }));
            }
            finally
            {
                // 安全恢复位置模式
                GTN.mc.GTN_SetEcatAxisMode(CORE, Axis_X, 8); // CSP模式
                GTN.mc.GTN_SetEcatAxisMode(CORE, Axis_Y, 8); // CSP模式
            }
        }

        // sat饱和函数：滑模控制边界层，降低抖振
        // sat(x)= clamp(x, -1, 1)
        private static double Sat(double x)
        {
            if (x > 1.0) return 1.0;
            if (x < -1.0) return -1.0;
            return x;
        }

        // 力矩限值
        public static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
        {
            if (value.CompareTo(min) < 0) return min;
            if (value.CompareTo(max) > 0) return max;
            return value;
        }

        // 高斯过程预测


        //// 窗体关闭事件处理
        //protected override void OnFormClosing(FormClosingEventArgs e)
        //{
        //    if (_controlThread != null && _controlThread.IsAlive)
        //    {
        //        _isSlidingControlActive = false;
        //        if (!_controlThread.Join(1000))
        //        {
        //            _controlThread.Interrupt();
        //        }
        //    }
        //    base.OnFormClosing(e);
        //}

        // -----------------------------------------------------------------------------------------------------------------------------------


        private void TrapcheckBox_CheckedChanged(object sender, EventArgs e)
        {
            JogcheckBox.Enabled = TrapcheckBox.Checked;
            if (TrapcheckBox.Checked)
            {
                JogcheckBox.Checked = false;
            }
        }

        private void button18_Click(object sender, EventArgs e)
        {
            if (button18.Text == "连接")
            {
                //IP地址和端口号输入不为空
                if (string.IsNullOrEmpty(textBox1.Text) == false && string.IsNullOrEmpty(textBox2.Text) == false)
                {
                    try
                    {
                        IPAddress ipAddress = IPAddress.Parse(textBox4.Text);//获取IP地址
                        int Port = Convert.ToInt32(textBox5.Text);  //获取端口号	
                        MySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        //使用 BeginConnect 异步连接
                        MySocket.BeginConnect(ipAddress, Port, new AsyncCallback(ConnectedCallback), MySocket);
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("IP地址或端口号错误!", "提示");
                    }
                }
                else
                {
                    MessageBox.Show("IP地址或端口号错误!", "提示");
                }
            }
            else
            {
                try
                {
                    button18.Text = "连接";
                    MySocket.BeginDisconnect(false, null, null);//断开连接
                    richTextBox1.AppendText("服务器连接断开\n");
                }
                catch (Exception) { }
            }
        }
        void ConnectedCallback(IAsyncResult ar)
        {
            Socket socket = (Socket)ar.AsyncState;
            try
            {
                socket.EndConnect(ar);
                Invoke((new Action(() =>
                {
                    MessageBox.Show("服务器连接成功", "提示");
                    button18.Text = "断开";
                })));

            }
            catch (Exception e)
            {
                Invoke((new Action(() =>
                {
                    MessageBox.Show("服务器连接失败", "提示");
                })));
            }
        }

        private void button19_Click(object sender, EventArgs e)
        {
            StringBuilder sb = new StringBuilder();
            // 删除文件
            if (File.Exists(strPath))
                File.Delete(strPath);

            // 创建文件
            if (!File.Exists(strPath))
                File.Create(strPath).Close();
            sb.AppendLine("TextBoxName,TextBoxValue");
            sb.AppendLine($"轴1脉冲当量,{dataGridView2.Rows[0].Cells[1].Value}");
            sb.AppendLine($"轴2脉冲当量,{dataGridView2.Rows[1].Cells[1].Value}");
            sb.AppendLine($"轴1传动比,{dataGridView2.Rows[0].Cells[2].Value}");
            sb.AppendLine($"轴2传动比,{dataGridView2.Rows[1].Cells[2].Value}");
            sb.AppendLine($"ip地址,{textBox4.Text}");
            sb.AppendLine($"端口号,{textBox5.Text}");
            File.WriteAllText(strPath, sb.ToString());
        }

        private void button20_Click(object sender, EventArgs e)
        {
            rtn = GTN.mc.GTN_ZeroPos(CORE, Axis_X, 2);
        }


        #endregion

        //点动Jog
        private short AxisJog(short axis, double vel)
        {
            double[] velConversionFactors = { 0, Axis1CONVERSION_FACTOR_Vel, Axis2CONVERSION_FACTOR_Vel };
            GTN.mc.TJogPrm jog;//Jog 参数
            jog.acc = 10;//加速度
            jog.dec = 10;//减速度
            jog.smooth = 0.5;//平滑时间
            rtn = GTN.mc.GTN_PrfJog(CORE, axis); // 设置为Jog运动模式
            rtn = GTN.mc.GTN_GetJogPrm(CORE, axis, out mc.TJogPrm jogPrm);//读取Jog运动参数
            rtn = GTN.mc.GTN_SetJogPrm(CORE, axis, ref jog); // 设置Jog运动参数
            rtn = GTN.mc.GTN_SetVel(CORE, axis, vel * velConversionFactors[axis]);  // 设置目标速度
            rtn = GTN.mc.GTN_Update(CORE, 1 << (axis - 1));    // 更新轴运动
            return rtn;
        }
        //寸动Trap
        private short AxisTrap(short axis, int armpos, double vel)
        {
            int[] posConversionFactors = { 0, (int)Axis1CONVERSION_FACTOR_Pos, (int)Axis2CONVERSION_FACTOR_Pos };
            double[] velConversionFactors = { 0, Axis1CONVERSION_FACTOR_Vel, Axis2CONVERSION_FACTOR_Vel };
            GTN.mc.TTrapPrm trap;//Trap参数
            trap.acc = 10;//加速度
            trap.dec = 10;//减速度
            trap.velStart = 0;//启动速度
            trap.smoothTime = 1;//平滑时间
            rtn = GTN.mc.GTN_PrfTrap(CORE, axis); // 设置为点位运动模式
            rtn = GTN.mc.GTN_SetTrapPrm(CORE, axis, ref trap); // 设置点位运动参数
            rtn = GTN.mc.GTN_SetVel(CORE, axis, vel * velConversionFactors[axis]); // 设置AXIS轴的目标速度
            rtn = GTN.mc.GTN_SetPos(CORE, axis, armpos * posConversionFactors[axis]);// 设置目标位置
            rtn = GTN.mc.GTN_Update(CORE, 1 << (axis - 1));// 启动AXIS轴的运动
            return rtn;
        }

        #region X轴点动
        // 按键按下启动负向运动
        private void button3_MouseDown(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = AxisJog(Axis_X, -Convert.ToDouble(this.textBox2.Text));
            }
        }
        // 按键抬起停止负向运功
        private void button3_MouseUp(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = GTN.mc.GTN_Stop(CORE, 1 << (Axis_X - 1), 0);
            }
        }
        // 按键按下启动正向运动
        private void button4_MouseDown(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = AxisJog(Axis_X, Convert.ToDouble(this.textBox2.Text));
            }
        }
        // 按键抬起停止正向运功
        private void button4_MouseUp(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = GTN.mc.GTN_Stop(CORE, 1 << (Axis_X - 1), 0);
            }
        }
        #endregion

        #region Y轴点动
        // 按键按下启动负向运动
        private void button5_MouseDown(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = AxisJog(Axis_Y, -Convert.ToDouble(this.textBox2.Text));
            }
        }


        // 按键抬起停止负向运功
        private void button5_MouseUp(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = GTN.mc.GTN_Stop(CORE, 1 << (Axis_Y - 1), 0);
            }
        }
        // 按键按下启动正向运动
        private void button6_MouseDown(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = AxisJog(Axis_Y, Convert.ToDouble(this.textBox2.Text));
            }
        }

        // 按键抬起停止正向运功
        private void button6_MouseUp(object sender, MouseEventArgs e)
        {
            if (JogcheckBox.Checked && !TrapcheckBox.Checked)
            {
                rtn = GTN.mc.GTN_Stop(CORE, 1 << (Axis_Y - 1), 0);
            }
        }
        //参数保存


        #endregion

        #region X轴寸动
        //X负向运动
        private void button3_Click(object sender, EventArgs e)
        {
            if (TrapcheckBox.Checked && !JogcheckBox.Checked)
            {
                rtn = mc.GTN_GetEncPos(CORE, Axis_X, out encpos_x, 1, out clk);
                rtn = AxisTrap(Axis_X, Convert.ToInt32((encpos_x / Axis1CONVERSION_FACTOR_Pos) - Convert.ToDouble(this.textBox3.Text)), Convert.ToDouble(this.textBox2.Text));
            }
        }
        //X正向运动
        private void button4_Click(object sender, EventArgs e)
        {
            if (TrapcheckBox.Checked && !JogcheckBox.Checked)
            {
                rtn = mc.GTN_GetEncPos(CORE, Axis_X, out encpos_x, 1, out clk);
                rtn = AxisTrap(Axis_X, Convert.ToInt32(Convert.ToDouble(this.textBox3.Text) + (encpos_x / Axis1CONVERSION_FACTOR_Pos)), Convert.ToDouble(this.textBox2.Text));
            }
        }
        #endregion

        #region Y轴寸动
        //Y负向运动
        private void button5_Click(object sender, EventArgs e)
        {
            if (TrapcheckBox.Checked && !JogcheckBox.Checked)
            {
                rtn = mc.GTN_GetEncPos(CORE, Axis_Y, out encpos_y, 1, out clk);
                rtn = AxisTrap(Axis_Y, Convert.ToInt32((encpos_y / Axis2CONVERSION_FACTOR_Pos) - Convert.ToDouble(this.textBox3.Text)), Convert.ToDouble(this.textBox2.Text));
            }
        }
        //Y正向运动
        private void button6_Click(object sender, EventArgs e)
        {
            if (TrapcheckBox.Checked && !JogcheckBox.Checked)
            {
                rtn = mc.GTN_GetEncPos(CORE, Axis_Y, out encpos_y, 1, out clk);
                rtn = AxisTrap(Axis_Y, Convert.ToInt32(Convert.ToDouble(this.textBox3.Text) + (encpos_y / Axis2CONVERSION_FACTOR_Pos)), Convert.ToDouble(this.textBox2.Text));
            }
        }
        #endregion
        #region 配方加载及运动
        private void button7_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Excel Files|*.xlsx;*.xls";
                openFileDialog.Title = "选择Excel文件";

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    string filePath = openFileDialog.FileName;
                    // 创建两个数据表分别存储轴1和轴2的数据
                    DataTable dataTableAxis1 = new DataTable();
                    DataTable dataTableAxis2 = new DataTable();

                    using (var package = new ExcelPackage(new FileInfo(filePath)))
                    {
                        ExcelWorksheet worksheet = package.Workbook.Worksheets[0];
                        int rowCount = worksheet.Dimension.Rows;
                        int colCount = worksheet.Dimension.Columns;

                        // 添加列到两个DataTable（假设第一行是列标题）
                        for (int col = 1; col <= colCount; col++)
                        {
                            dataTableAxis1.Columns.Add(worksheet.Cells[1, col].Text);
                            dataTableAxis2.Columns.Add(worksheet.Cells[1, col].Text);
                        }

                        // 从第二行开始读取每一行数据
                        for (int row = 2; row <= rowCount; row++)
                        {
                            int axisIndex = 0;
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(worksheet.Cells[row, 1].Text))
                                {
                                    axisIndex = Convert.ToInt16(worksheet.Cells[row, 1].Text); // 假设轴号在第一列
                                }
                            }
                            catch (FormatException)
                            {
                                // 如果转换失败，axisIndex 保持为 0
                                axisIndex = 0;
                            }
                            catch (OverflowException)
                            {
                                // 如果转换失败，axisIndex 保持为 0
                                axisIndex = 0;
                            }
                            //int axisIndex = Convert.ToInt16(worksheet.Cells[row, 1].Text); // 假设轴号在第一列

                            if (axisIndex == 1)
                            {
                                DataRow dataRow = dataTableAxis1.NewRow();
                                for (int col = 1; col <= colCount; col++)
                                {
                                    dataRow[col - 1] = worksheet.Cells[row, col].Text;
                                }
                                dataTableAxis1.Rows.Add(dataRow);
                            }
                            else if (axisIndex == 2)
                            {
                                DataRow dataRow = dataTableAxis2.NewRow();
                                for (int col = 1; col <= colCount; col++)
                                {
                                    dataRow[col - 1] = worksheet.Cells[row, col].Text;
                                }
                                dataTableAxis2.Rows.Add(dataRow);
                            }
                        }
                        if (dataTableAxis1.Rows.Count > 0)
                        {
                            dataGridView3.Rows.Clear();
                            // 将dataTableAxis1的数据填充到dataGridView3
                            foreach (DataRow row in dataTableAxis1.Rows)
                            {
                                DataGridViewRow dataGridViewRow = new DataGridViewRow();
                                dataGridViewRow.CreateCells(dataGridView3);

                                for (int col = 0; col < dataTableAxis1.Columns.Count; col++)
                                {
                                    dataGridViewRow.Cells[col].Value = row[col];
                                }

                                dataGridView3.Rows.Add(dataGridViewRow);
                            }
                        }

                        if (dataTableAxis2.Rows.Count > 0)
                        {
                            dataGridView4.Rows.Clear();

                            // 将dataTableAxis2的数据填充到dataGridView4
                            foreach (DataRow row in dataTableAxis2.Rows)
                            {
                                DataGridViewRow dataGridViewRow = new DataGridViewRow();
                                dataGridViewRow.CreateCells(dataGridView4);

                                for (int col = 0; col < dataTableAxis2.Columns.Count; col++)
                                {
                                    dataGridViewRow.Cells[col].Value = row[col];
                                }

                                dataGridView4.Rows.Add(dataGridViewRow);
                            }
                        }
                    }
                }
            }
        }//加载配方
        private void Data()//将dataGridView数据导入DataTable中方便运行数据提取
        {
            double[] posConversionFactors = { 0, Axis1CONVERSION_FACTOR_Pos, Axis2CONVERSION_FACTOR_Pos };
            double[] velConversionFactors = { 0, Axis1CONVERSION_FACTOR_Vel, Axis2CONVERSION_FACTOR_Vel };
            DataTable dataTable1 = new DataTable();
            DataTable dataTable2 = new DataTable();

            // 添加列到 dataTable1 (轴1数据表)
            for (int i = 0; i < dataGridView3.Columns.Count; i++)
            {
                dataTable1.Columns.Add(dataGridView3.Columns[i].HeaderText);
            }

            // 添加行数据到 dataTable1 (轴1数据表)
            foreach (DataGridViewRow row in dataGridView3.Rows)
            {
                if (row.Visible && !row.IsNewRow) // 排除新行
                {
                    DataRow dataRow = dataTable1.NewRow();
                    for (int j = 0; j < row.Cells.Count; j++)
                    {
                        dataRow[j] = row.Cells[j].Value;
                    }
                    dataTable1.Rows.Add(dataRow);
                }
            }

            // 添加列到 dataTable2 (轴2数据表)
            for (int i = 0; i < dataGridView4.Columns.Count; i++)
            {
                dataTable2.Columns.Add(dataGridView4.Columns[i].HeaderText);
            }

            // 添加行数据到 dataTable2 (轴2数据表)
            foreach (DataGridViewRow row in dataGridView4.Rows)
            {
                if (row.Visible && !row.IsNewRow) // 排除新行
                {
                    DataRow dataRow = dataTable2.NewRow();
                    for (int j = 0; j < row.Cells.Count; j++)
                    {
                        dataRow[j] = row.Cells[j].Value;
                    }
                    dataTable2.Rows.Add(dataRow);
                }
            }

            // 初始化轴1的数据数组
            dataAxis1 = new short[dataTable1.Rows.Count];
            dataPos1 = new double[dataTable1.Rows.Count];
            dataVel1 = new double[dataTable1.Rows.Count];
            dataTime1 = new double[dataTable1.Rows.Count];

            for (int i = 0; i < dataTable1.Rows.Count; i++)
            {
                DataRow row = dataTable1.Rows[i];
                dataAxis1[i] = Convert.ToInt16(row["轴号"]);
                dataPos1[i] = Convert.ToDouble(row["终点位置"]) * posConversionFactors[dataAxis1[i]];
                dataVel1[i] = Convert.ToDouble(row["终点速度"]) * velConversionFactors[dataAxis1[i]];
                dataTime1[i] = Convert.ToDouble(row["时间"]);
            }

            // 初始化轴2的数据数组
            dataAxis2 = new short[dataTable2.Rows.Count];
            dataPos2 = new double[dataTable2.Rows.Count];
            dataVel2 = new double[dataTable2.Rows.Count];
            dataTime2 = new double[dataTable2.Rows.Count];

            // 填充轴2的数据数组
            for (int i = 0; i < dataTable2.Rows.Count; i++)
            {
                DataRow row = dataTable2.Rows[i];
                dataAxis2[i] = Convert.ToInt16(row["轴号"]);
                dataPos2[i] = Convert.ToDouble(row["终点位置"]) * posConversionFactors[dataAxis2[i]];
                dataVel2[i] = Convert.ToDouble(row["终点速度"]) * velConversionFactors[dataAxis2[i]];
                dataTime2[i] = Convert.ToDouble(row["时间"]);
            }

            int MaxDataPerTable = 1024;
            // 计算轴1和轴2需要的数据表数量
            Axis1tableCount = (short)(dataTable1.Rows.Count / MaxDataPerTable);
            if (dataTable1.Rows.Count % MaxDataPerTable != 0) Axis1tableCount++; // 如果有余数，需要额外的数据表

            Axis2tableCount = (short)(dataTable2.Rows.Count / MaxDataPerTable);
            if (dataTable2.Rows.Count % MaxDataPerTable != 0) Axis2tableCount++; // 如果有余数，需要额外的数据表

            Axis1runTable = 1;
            Axis2runTable = (short)(Axis1tableCount + 1);
            // 轴1数据表
            for (int i = 0; i < Axis1tableCount; i++)
            {
                int k = i * MaxDataPerTable;
                int dataLength = (i == Axis1tableCount - 1) ? dataTable1.Rows.Count - k : MaxDataPerTable;
                rtn = GTN.mc.GTN_PvtTable(1, (short)(Axis1runTable + i), dataLength, ref dataTime1[k], ref dataPos1[k], ref dataVel1[k]);
            }

            // 分配和填充轴2的数据表
            for (int i = 0; i < Axis2tableCount; i++)
            {
                int k = i * MaxDataPerTable;
                int dataLength = (i == Axis2tableCount - 1) ? dataTable2.Rows.Count - k : MaxDataPerTable;
                rtn = GTN.mc.GTN_PvtTable(1, (short)(Axis2runTable + i), dataLength, ref dataTime2[k], ref dataPos2[k], ref dataVel2[k]);
            }


        }
        #endregion

        private void button10_Click(object sender, EventArgs e)
        {
            if (button10.Text == "启动")
            {

                Data();
                byte[] buffer = new byte[1];
                buffer[0] = 1;
                // 检查 MySocket 是否已经连接
                if (MySocket != null && MySocket.Connected)
                {
                    MySocket.BeginSend(buffer, 0, 1, 0, null, null);
                }
                // 设置为PVT模式
                rtn = GTN.mc.GTN_PrfPvt(1, Axis_X);
                rtn = GTN.mc.GTN_PrfPvt(1, Axis_Y);
                // 选择数据表
                rtn = GTN.mc.GTN_PvtTableSelect(1, Axis_X, Axis1runTable);
                rtn = GTN.mc.GTN_PvtTableSelect(1, Axis_Y, Axis2runTable);
                rtn = GTN.mc.GTN_PvtStart(1, 3);
                if (rtn == 0)
                {
                    row = 2;
                    count = 2;
                    time = 0;

                    // 启动真实时间戳记录
                    _sw.Restart();
                    _tsMs.Clear();
                    _lastWrittenIndex = 0;
                    _tsBaseMs = 0;
                    _startIndexForThisFile = axis1PosList.Count;

                    timer3.Interval = Convert.ToInt32(textBox1.Text);
                    writePlaying = true;
                    timer3.Start();
                    Dataswitch = true;
                }
                button10.Text = "停止";
                if (checkBox1.Checked == true)
                {
                    int fileIndex = 1;
                    string folderPath = "数据"; // 数据文件夹名称
                    while (File.Exists(filePath))
                    {
                        fileIndex++;
                        filePath = Path.Combine(folderPath, "data" + fileIndex.ToString() + ".xlsx");
                    }
                    //row = 2;
                    //count = 2;
                    //time = 0;

                    //// 启动真实时间戳记录
                    //_sw.Restart();
                    //_tsMs.Clear();
                    //_lastWrittenIndex = 0;
                    //_tsBaseMs = 0;
                    //_startIndexForThisFile = axis1PosList.Count;

                    //timer3.Interval = Convert.ToInt32(textBox1.Text);
                    //writePlaying = true;
                    //timer3.Start();
                }

            }
            else if (button10.Text == "停止")
            {
                // 停止操作
                rtn = GTN.mc.GTN_Stop(CORE, 3, 0);
                if (rtn == 0)
                {
                    Dataswitch = false;
                }
                if (checkBox1.Checked == true)
                {
                    writePlaying = false;
                }

                // 将按钮文本改为 "Start"
                button10.Text = "启动";
            }



        }
        private void button8_Click(object sender, EventArgs e)
        {

            int fileIndex = 1;
            string folderPath = "数据"; // 数据文件夹名称
            while (File.Exists(filePath))
            {
                fileIndex++;
                filePath = Path.Combine(folderPath, "data" + fileIndex.ToString() + ".xlsx");
            }
            row = 2;
            count = 2;
            time = 0;
            // 启动真实时间戳记录
            _sw.Restart();
            _tsMs.Clear();
            _lastWrittenIndex = 0;
            _tsBaseMs = 0;
            _startIndexForThisFile = axis1PosList.Count;

            timer3.Interval = Convert.ToInt32(textBox1.Text);
            writePlaying = true;
            timer3.Start();
        }
        private void button9_Click(object sender, EventArgs e)
        {
            writePlaying = false;
        }
        // 写入 Excel 文件
        private void WriteDataToExcel(List<double> axis1PosList, List<double> axis1VelList, List<double> axis1TorqueList,
                                 List<double> axis2PosList, List<double> axis2VelList, List<double> axis2TorqueList)
        {

            // 计算可写入的样本总数（按最小长度防越界）
            int total = new int[]
            {
                axis1PosList.Count, axis1VelList.Count, axis1TorqueList.Count,
               axis2PosList.Count, axis2VelList.Count, axis2TorqueList.Count,
                _tsMs.Count
            }.Min();
            // 从上次写入位置开始逐条写入，保证每个样本都有自己的真实时间戳
            for (int k = _lastWrittenIndex; k < total; k++)
            {
                int r = row + k; // Excel 行号
                excelWorksheet.Cells[r, 1].Value = axis1PosList[k];
                excelWorksheet.Cells[r, 2].Value = axis1VelList[k];
                excelWorksheet.Cells[r, 3].Value = axis1TorqueList[k];
                excelWorksheet.Cells[r, 4].Value = axis2PosList[k];
                excelWorksheet.Cells[r, 5].Value = axis2VelList[k];
                excelWorksheet.Cells[r, 6].Value = axis2TorqueList[k];
                excelWorksheet.Cells[r, 7].Value = _tsMs[k]; // 真实毫秒时间戳
                                                             // 可选：第8列写相邻采样的实际周期
                                                             //if (k > 0) excelWorksheet.Cells[r, 8].Value = _tsMs[k] - _tsMs[k - 1];
            }
            _lastWrittenIndex = total;
            //if (!writePlaying)
            //{
            //    // 保存 Excel 文件
            //    FileInfo excelFile = new FileInfo(filePath);
            //    excelPackage.SaveAs(excelFile);
            //    timer3.Stop();
            //}


        }
        int Sts1, Sts2;
        uint clock;
        private void timer3_Tick(object sender, EventArgs e)
        {
            WriteDataToExcel(axis1PosList, axis1VelList, axis1TorqueList, axis2PosList, axis2VelList, axis2TorqueList);

            GTN.mc.GT_GetSts(1, out Sts1, 1, out clock);
            GTN.mc.GT_GetSts(2, out Sts2, 1, out clock);

            if (((Sts1 & 0x400) == 0) && ((Sts2 & 0x400) == 0))
            {
                FileInfo excelFile = new FileInfo(filePath);
                excelPackage.SaveAs(excelFile);
                timer3.Stop();
                writePlaying = false;
                button10.Text = "启动";
            }
        }

    }


}

