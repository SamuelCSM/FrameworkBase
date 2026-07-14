using System.Collections.Generic;
using Editor.ExcelTool;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 配表校验链路门禁（模板垂直切片 B）：证明 ExcelDataValidator 会在导出期拦下非法配置，
    /// 而不是把脏数据静默写进 config.db。内存构造工作表数据，不依赖 Excel 文件夹具。
    ///
    /// 这是「配表流水线含校验」这一承诺的回归守卫：ConfigPipeline 以 EnableValidation=true 导出，
    /// 校验不过的表会让导出失败（ExcelExporter 返回 !Success，管线抛异常打红）。
    /// </summary>
    public class ConfigValidatorTests
    {
        /// <summary>构造一张合法的 clicker_level 工作表数据（首列 Id 为主键）。</summary>
        private static ExcelReader.ExcelSheetData BuildValidSheet()
        {
            var sheet = new ExcelReader.ExcelSheetData
            {
                SheetName = "clicker_level",
                SheetKind = ExcelReader.ExcelSheetKind.Table,
                FieldNames = new List<string> { "Id", "ClickGain", "Name" },
                TypeDefinitions = new List<string> { "int", "int", "string" },
                Comments = new List<string> { "等级", "点击收益", "名称" },
            };
            sheet.DataRows.Add(new Dictionary<string, object> { { "Id", 1 }, { "ClickGain", 1 }, { "Name", "新手" } });
            sheet.DataRows.Add(new Dictionary<string, object> { { "Id", 2 }, { "ClickGain", 2 }, { "Name", "学徒" } });
            return sheet;
        }

        [Test]
        public void ValidSheet_PassesValidation()
        {
            var result = new ExcelDataValidator().ValidateSheet(BuildValidSheet());
            Assert.IsTrue(result.IsValid,
                "合法工作表不应被校验拒绝，错误: " + string.Join("; ", result.Errors));
        }

        [Test]
        public void DuplicatePrimaryKey_IsRejected()
        {
            ExcelReader.ExcelSheetData sheet = BuildValidSheet();
            // 注入非法值：主键 Id 重复（第二行也用 1）。导出期必须拦下，否则运行时按主键索引会丢行。
            sheet.DataRows[1]["Id"] = 1;

            var result = new ExcelDataValidator().ValidateSheet(sheet);

            Assert.IsFalse(result.IsValid, "主键重复的工作表必须被校验拒绝");
            CollectionAssert.IsNotEmpty(result.Errors);
            StringAssert.Contains("主键重复", string.Join("; ", result.Errors));
        }

        [Test]
        public void EmptyPrimaryKey_IsRejected()
        {
            ExcelReader.ExcelSheetData sheet = BuildValidSheet();
            // 注入非法值：主键为空。
            sheet.DataRows[1]["Id"] = null;

            var result = new ExcelDataValidator().ValidateSheet(sheet);

            Assert.IsFalse(result.IsValid, "主键为空的工作表必须被校验拒绝");
            StringAssert.Contains("主键", string.Join("; ", result.Errors));
        }
    }
}
