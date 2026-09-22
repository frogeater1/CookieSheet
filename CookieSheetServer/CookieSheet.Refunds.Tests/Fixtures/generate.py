"""Regenerate synthetic Excel fixtures with xlwt 1.3.0 and openpyxl (test-time dependencies only)."""
from datetime import datetime
from pathlib import Path

import openpyxl
import xlwt

root = Path(__file__).parent
date = datetime(2026, 9, 18, 15, 52, 35)
orders = [
    ["下单模板信息", "收货人/提货人手机号", "收货人/提货人姓名", "下单账号", "收货/提货详细地址",
     "商品名称", "商品型号", "商品件数", "商品ID", "商品单价", "运费", "实付金额", "订单编号",
     "下单时间", "发货时间", "退款状态", "订单状态"],
    ["QQ:00123456;CN:测试", "13800138000", "测试收件人", "测试账号", "测试地址",
     "企划", "明信片", 5, "00123", 7, 8, 43.5, "00144115526827130368", date, None, "", "待发货"],
    [None] * 17,
    ["", 13800138001, "另一位收件人", "账号二", "地址二", "色纸;挂件", "", "2;3", "p1;p2",
     "10;5", 0, 35, "order-2", "2026-09-19 10:20:30", "", "退款完成", "已关闭"],
]
refunds = [
    ["订单编号", "退款编号", "商品名称", "商品型号", "商品实付金额", "买家退款金额", "退款状态", "退款的申请时间"],
    ["00144115526827130368", "144115526827130368", "企划", "明信片", 35, 7.5, "退款成功", date],
    ["00144115526827130368", "144115526827130368", "企划", "明信片", 35, 7.5, "退款成功", date],
    ["", "", "", "", "无效", "无效", "退款完成", ""],
]

for name, rows in [("orders", orders), ("refunds", refunds)]:
    legacy = xlwt.Workbook(encoding="utf-8")
    sheet = legacy.add_sheet("导入数据")
    date_style = xlwt.easyxf(num_format_str="yyyy-mm-dd hh:mm:ss")
    for r, values in enumerate(rows):
        for c, value in enumerate(values):
            sheet.write(r, c, value, date_style if isinstance(value, datetime) else xlwt.Style.default_style)
    legacy.save(str(root / f"{name}.xls"))

    modern = openpyxl.Workbook()
    sheet = modern.active
    sheet.title = "导入数据"
    for values in rows:
        sheet.append(values)
    modern.save(root / f"{name}.xlsx")
