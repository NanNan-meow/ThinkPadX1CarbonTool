界面文案精简，并增加实机测试入口。

- 删除重复的硬件条件、等待说明和防御性叙述，保留读数、操作和简短状态。
- 电源模式按钮直接显示睿频与风扇搭配。
- 包内新增 `TestHardware.cmd`，退出托盘程序后以管理员身份运行。
- 实机测试记录电池、SSD、CPU 负载频率、电源方案读回、风扇档位与 RPM 响应。
- 测试结束后恢复原电源方案和风扇模式，报告保存在 `reports`。

672 项逻辑断言及界面检查通过。

X1 Carbon Gen 8 / i5-10310U 管理员实测：近 99% CPU 负载下，低温模式平均 2.21 GHz / 5.85 W，性能模式平均 4.01 GHz / 22.23 W；风扇从 2678 RPM 升至约 4687 RPM。电池、SSD、三种电源模式、风扇联动和原设置恢复均取得实测读回。详见[实机验证记录](https://github.com/NanNan-meow/ThinkPadX1CarbonTool/blob/v1.4.1/docs/HARDWARE-VALIDATION.md)。
