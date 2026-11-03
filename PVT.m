clc;
clear;

% 参数设置
T = 50; % 总时间
fs = 100; % 采样频率
t = 0:1/fs:T-1/fs; % 时间向量
A = 15; % 振幅
omega = 2*pi/T; % 角频率

% 生成位置函数（正弦函数）
position = A * sin(omega * t);

% 求导得到速度函数
velocity = A * omega * cos(omega * t);

% 绘制位置和速度曲线
figure;
subplot(2,1,1);
plot(t, position, 'b', 'LineWidth', 1.5);
title('位置曲线');
xlabel('时间 (s)');
ylabel('位置');
grid on;

subplot(2,1,2);
plot(t, velocity, 'r', 'LineWidth', 1.5);
title('速度曲线');
xlabel('时间 (s)');
ylabel('速度');
grid on;

% 对位置和速度的离散点采样
sample_rate = 2; % 采样率
sample_indices = 1:sample_rate:length(position); % 采样索引
sampled_position = position(sample_indices);
sampled_velocity = velocity(sample_indices);

% 绘制采样点
figure;
subplot(2,1,1);
stem(t(sample_indices), sampled_position, 'b', 'LineWidth', 1.5);
title('位置采样点');
xlabel('时间 (s)');
ylabel('位置');
grid on;

subplot(2,1,2);
stem(t(sample_indices), sampled_velocity, 'r', 'LineWidth', 1.5);
title('速度采样点');
xlabel('时间 (s)');
ylabel('速度');
grid on;

new_t=t(sample_indices)'*1000;
new_sampled_position=(sampled_position)';
new_sampled_velocity=(sampled_velocity)';

new_t = double(new_t);
new_sampled_position = double(new_sampled_position);
new_sampled_velocity = double(new_sampled_velocity);