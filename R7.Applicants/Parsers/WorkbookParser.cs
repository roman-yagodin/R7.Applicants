using System;
using System.IO;
using System.Text.RegularExpressions;
using LiteDB;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using R7.Applicants.Models;

namespace R7.Applicants.Parsers
{
    // TODO: Move regexes to separate configuration class?
    public class WorkbookParser
    {
        ILiteDatabase db;

        ILiteCollection<Division> divisions;
        ILiteCollection<EduForm> eduForms;
        ILiteCollection<Financing> financings;
        ILiteCollection<EduProgram> eduPrograms;
        ILiteCollection<EduLevel> eduLevels;
        ILiteCollection<Applicant> applicants;
        ILiteCollection<SourceFile> sourceFiles;

        public WorkbookParser (ILiteDatabase db)
        {
            this.db = db;

            divisions = db.GetCollection<Division> ("Divisions");
            eduForms = db.GetCollection<EduForm> ("EduForms");
            financings = db.GetCollection<Financing> ("Financings");
            eduPrograms = db.GetCollection<EduProgram> ("EduPrograms");
            eduLevels = db.GetCollection<EduLevel> ("EduLevels");
            applicants = db.GetCollection<Applicant> ("Applicants");
            sourceFiles = db.GetCollection<SourceFile> ("SourceFiles");
        }

        /// <summary>
        /// Parses the cell, possibly multiple times according to the state changes.
        /// </summary>
        /// <param name="cell">Cell.</param>
        /// <param name="cellStrValue">Cell string value.</param>
        public void ParseCell (ICell cell, string cellStrValue, CellRangeAddress cellRangeAddress, WorkbookParserContext context, out bool skipRow)
        {
            var parseCellAgain = false;
            do {
                ParseCellOnce (cell, cellStrValue, cellRangeAddress, context, out parseCellAgain, out skipRow);
            } while (parseCellAgain);
        }

        public void ParseCellOnce (ICell cell, string cellStrValue, CellRangeAddress cellRangeAddress, WorkbookParserContext context, out bool parseCellAgain, out bool skipRow)
        {
            parseCellAgain = false;
            skipRow = false;

            if (context.State == WorkbookParserState.Initial) {
                context.State = WorkbookParserState.Header;
                parseCellAgain = true;
                return;
            }

            if (context.State == WorkbookParserState.Header) {
                if (!cell.IsMergedCell) {
                    if (cell.ColumnIndex == 0 && cellStrValue.Equals ("№ п/п", StringComparison.CurrentCultureIgnoreCase)) {
                        context.State = WorkbookParserState.TableHeader;
                        return;
                    }
                }
                else if (cell.IsMergedCell && cellRangeAddress.NumberOfCells >= 10) {
                    if (Regex.IsMatch (cellStrValue, "факультет|институт", RegexOptions.IgnoreCase)) {
                        var divisionTitle = cellStrValue.ToLower ();
                        var division = divisions.FindOne (d => d.Title == divisionTitle);
                        if (division == null) {
                            division = new Division {
                                Title = divisionTitle
                            };
                            var id = divisions.Insert (division);
                            db.Commit ();
                            division.Id = id.AsInt32;
                        }
                        context.Division = division;
                    }
                    if (Regex.IsMatch (cellStrValue, "форма обучения", RegexOptions.IgnoreCase)) {
                        var eduForm = eduForms.FindOne (ef => ef.Title == cellStrValue);
                        if (eduForm == null) {
                            eduForm = new EduForm {
                                Title = cellStrValue
                            };
                            var id = eduForms.Insert (eduForm);
                            db.Commit ();
                            eduForm.Id = id.AsInt32;
                        }
                        context.EduForm = eduForm;
                    }
                    if (Regex.IsMatch (cellStrValue, "бюджет|договор", RegexOptions.IgnoreCase)) {
                        var financingTitle = cellStrValue.ToLower ();
                        var financing = financings.FindOne (f => f.Title == financingTitle);
                        if (financing == null) {
                            financing = new Financing {
                                Title = financingTitle
                            };
                            var id = financings.Insert (financing);
                            db.Commit ();
                            financing.Id = id.AsInt32;
                        }
                        context.Financing = financing;
                    }
                    if (Regex.IsMatch (cellStrValue, "бакалавриат|специалитет|магистратур|подготовки кадров|основного общего|среднего общего", RegexOptions.IgnoreCase)) {
                        var eduLevelStrValue = GetEduLevelString (cellStrValue);

                        var eduLevel = eduLevels.FindOne (el => el.Title == eduLevelStrValue);
                        if (eduLevel == null) {
                            eduLevel = new EduLevel {
                                Title = eduLevelStrValue
                            };
                            var id = eduLevels.Insert (eduLevel);
                            db.Commit ();
                            eduLevel.Id = id.AsInt32;
                        }
                        context.EduLevel = eduLevel;

                        if (context.EduLevel.Title.StartsWith ("специалитет СПО", StringComparison.CurrentCultureIgnoreCase)) {
                            context.IsCollegeList = true;
                        }
                        else {
                            context.IsCollegeList = false;
                        }

                        var eduProgramTitle = Regex.Match (cellStrValue, @"«[^»]+»", RegexOptions.Singleline | RegexOptions.IgnoreCase).Value;
                        var eduProfileTitle = Regex.Match (cellStrValue, @"Профиль:(.*)", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups [1].Value;
                        if (string.IsNullOrEmpty (eduProfileTitle)) {
                            eduProfileTitle = Regex.Match (cellStrValue, @"на базе .* общего образования", RegexOptions.Singleline | RegexOptions.IgnoreCase).Value;
                        }

                        eduProgramTitle = FormatEduProgramTitle (eduProgramTitle);
                        eduProfileTitle = FormatEduProgramTitle (eduProfileTitle);

                        if (context.EduProgram == null) {
                            context.EduProgram = new EduProgram ();
                        }

                        context.EduProgram.Title = eduProgramTitle;
                        context.EduProgram.ProfileTitle = eduProfileTitle;
                        context.EduProgram.EduLevelId = context.EduLevel.Id;
                        context.EduProgram.DivisionId = context.Division.Id;
                    }
                }
                return;
            }

            if (context.State == WorkbookParserState.TableHeader && context.IsCollegeList) {
                if (cell.ColumnIndex == 5) {
                    context.EduProgram.Exam1Title = cellStrValue;
                }
                else if (cell.ColumnIndex == 7) {
                    context.EduProgram.Exam2Title = cellStrValue;
                }
                else if (cell.ColumnIndex > 7) {
                    context.State = WorkbookParserState.List;
                    context.Order = 0;
                    skipRow = true;
                    InsertEduProgram (context);
                }
                return;
            }

            if (context.State == WorkbookParserState.TableHeader && !context.IsCollegeList) {
                if (cell.ColumnIndex == 4) {
                    context.EduProgram.Exam1Title = cellStrValue;
                }
                else if (cell.ColumnIndex == 5) {
                    context.EduProgram.Exam2Title = cellStrValue;
                }
                else if (cell.ColumnIndex == 6) {
                    context.EduProgram.Exam3Title = cellStrValue;
                }
                else if (cell.ColumnIndex > 6) {
                    context.State = WorkbookParserState.List;
                    context.Order = 0;
                    skipRow = true;
                    InsertEduProgram (context);
                }
                return;
            }

            if (context.State == WorkbookParserState.List) {
                if (cell.IsMergedCell && cellRangeAddress.NumberOfCells >= 10) {
                    context.State = WorkbookParserState.Header;
                    parseCellAgain = true;
                    return;
                }
                if (cell.ColumnIndex == 0) {
                    context.Applicant = new Applicant ();
                    context.Applicant.Order = ++context.Order;
                    if (int.TryParse (cellStrValue, out int rankedOrder)) {
                        context.Applicant.RankedOrder = rankedOrder;
                    }
                }
                else if (cell.ColumnIndex == 1) {
                    context.Applicant.Name = cellStrValue;
                }
            }

            if (context.State == WorkbookParserState.List && context.IsCollegeList) {
                if (cell.ColumnIndex == 3) {
                    context.Applicant.HasOriginal = cellStrValue.Equals ("Оригинал", StringComparison.CurrentCultureIgnoreCase);
                }
                else if (cell.ColumnIndex == 5) {
                    if (decimal.TryParse (cellStrValue, out decimal exam1Rate)) {
                        context.Applicant.Exam1Rate = exam1Rate;
                    }
                }
                else if (cell.ColumnIndex == 7) {
                    context.Applicant.Exam2Mark = cellStrValue;
                }
                else if (cell.ColumnIndex == 9) {
                    if (decimal.TryParse (cellStrValue, out decimal totalRate)) {
                        context.Applicant.TotalRate = totalRate;
                    }
                }
                else if (cell.ColumnIndex == 10) {
                    context.Applicant.Status = cellStrValue;
                }
                else if (cell.ColumnIndex == 11) {
                    context.Applicant.RejectReason = cellStrValue;
                }
                else if (cell.ColumnIndex > 11) {
                    context.Applicant.EduProgramId = context.EduProgram.Id;
                    context.Applicant.EduFormId = context.EduForm.Id;
                    context.Applicant.FinancingId = context.Financing.Id;
                    applicants.Insert (context.Applicant);
                    skipRow = true;
                }
                return;
            }

            if (context.State == WorkbookParserState.List && !context.IsCollegeList) {
                if (cell.ColumnIndex == 2) {
                    context.Applicant.HasOriginal = cellStrValue.Equals ("Оригинал", StringComparison.CurrentCultureIgnoreCase);
                }
                else if (cell.ColumnIndex == 3) {
                    context.Applicant.HasAgreement = cellStrValue.Equals ("Да", StringComparison.CurrentCultureIgnoreCase);
                }
                if (cell.ColumnIndex == 4) {
                    if (decimal.TryParse (cellStrValue, out decimal exam1Rate)) {
                        context.Applicant.Exam1Rate = exam1Rate;
                    }
                }
                else if (cell.ColumnIndex == 5) {
                    if (decimal.TryParse (cellStrValue, out decimal exam2Rate)) {
                        context.Applicant.Exam2Rate = exam2Rate;
                    }
                }
                else if (cell.ColumnIndex == 6) {
                    if (decimal.TryParse (cellStrValue, out decimal exam3Rate)) {
                        context.Applicant.Exam3Rate = exam3Rate;
                    }
                }
                else if (cell.ColumnIndex == 7) {
                    if (decimal.TryParse (cellStrValue, out decimal achRate)) {
                        context.Applicant.AchRate = achRate;
                    }
                }
                else if (cell.ColumnIndex == 8) {
                    if (decimal.TryParse (cellStrValue, out decimal totalRate)) {
                        context.Applicant.TotalRate = totalRate;
                    }
                }
                else if (cell.ColumnIndex == 9) {
                    context.Applicant.Category = cellStrValue;
                }
                else if (cell.ColumnIndex == 10) {
                    if (cellStrValue.Equals ("Да", StringComparison.CurrentCultureIgnoreCase)) {
                        context.Applicant.HasPreemptiveRight = true;
                    }
                }
                else if (cell.ColumnIndex == 11) {
                    context.Applicant.Status = cellStrValue;
                }
                else if (cell.ColumnIndex == 12) {
                    context.Applicant.RejectReason = cellStrValue;
                }
                else if (cell.ColumnIndex > 12) {
                    context.Applicant.EduProgramId = context.EduProgram.Id;
                    context.Applicant.EduFormId = context.EduForm.Id;
                    context.Applicant.FinancingId = context.Financing.Id;
                    applicants.Insert (context.Applicant);
                    skipRow = true;
                }
                return;
            }
        }

        public void ParseTo (string path, ILiteDatabase db)
        {
            var sourceFile = new SourceFile {
                Filename = Path.GetFileName (path),
                LastWriteTimeUtc = File.GetLastWriteTimeUtc (path),
                Length = new FileInfo (path).Length
            };
            sourceFiles.Insert (sourceFile);
            db.Commit ();

            using (var fileStream = new FileStream (path, FileMode.Open, FileAccess.Read)) {
                IWorkbook book;
                if (Path.GetExtension (path).ToLowerInvariant () == ".xls") {
                    book = new HSSFWorkbook (fileStream);
                }
                else if (Path.GetExtension (path).ToLowerInvariant () == ".xlsx") {
                    book = new XSSFWorkbook (fileStream);
                }
                else {
                    throw new ArgumentException ("Unsupported file type!");
                }

                var formatter = new DataFormatter ();
                var context = new WorkbookParserContext ();

                for (var s = 0; s < book.NumberOfSheets; s++) {
                    var sheet = book.GetSheetAt (s);
                    for (var r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++) {
                        var row = sheet.GetRow (r);
                        if (row == null) {
                            continue;
                        }
                        foreach (var cell in row.Cells) {
                            var cellStrValue = formatter.FormatCellValue (cell).Trim ();
                            var cellRangeAddress = GetCellRangeAddress (cell, sheet);
                            ParseCell (cell, cellStrValue, cellRangeAddress, context, out bool skipRow);
                            if (skipRow) {
                                break;
                            }
                        }
                    }
                }
            }
        }

        string FormatEduProgramTitle (string eduProgramTitle)
        {
            eduProgramTitle = eduProgramTitle.Replace ("«", "");
            eduProgramTitle = eduProgramTitle.Replace ("»", "");
            eduProgramTitle = eduProgramTitle.Replace ("\n", " ");
            eduProgramTitle = eduProgramTitle.Replace ("\r", " ");
            eduProgramTitle = eduProgramTitle.Replace ("\t", " ");
            eduProgramTitle = Regex.Replace (eduProgramTitle, @"\s+", " ");
            eduProgramTitle = eduProgramTitle.Trim ();
            return eduProgramTitle;
        }

        CellRangeAddress GetCellRangeAddress (ICell cell, ISheet sheet)
        {
            for (var i = 0; i < sheet.NumMergedRegions; i++) {
                var mergedRegion = sheet.GetMergedRegion (i);
                if (mergedRegion.IsInRange (cell.RowIndex, cell.ColumnIndex)) {
                    return mergedRegion;
                }
            }

            return null;
        }

        string GetEduLevelString (string cellStrValue)
        {
            if (Regex.IsMatch (cellStrValue, "бакалавриат", RegexOptions.IgnoreCase)) {
                return "бакалавриат";
            }
            if (Regex.IsMatch (cellStrValue, "магистратуры", RegexOptions.IgnoreCase)) {
                return "магистратура";
            }
            if (Regex.IsMatch (cellStrValue, "подготовки кадров высшей квалификации", RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
                return "аспирантура";
            }
            if (Regex.IsMatch (cellStrValue, "на базе .* общего образования", RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
                return "специалитет СПО";
            }
            if (Regex.IsMatch (cellStrValue, "специалитета", RegexOptions.IgnoreCase)) {
                return "специалитет";
            }

            return null;
        }

        void InsertEduProgram (WorkbookParserContext context)
        {
            var eduProgram = eduPrograms.FindOne (ep => ep.Title == context.EduProgram.Title
                                && ep.ProfileTitle == context.EduProgram.ProfileTitle
                                && ep.EduLevelId == context.EduLevel.Id
                                && ep.DivisionId == context.Division.Id);

            if (eduProgram == null) {
                eduProgram = context.EduProgram;
                eduProgram.Id = 0;
                var id = eduPrograms.Insert (eduProgram);
                db.Commit ();
                eduProgram.Id = id.AsInt32;
            }
            context.EduProgram = eduProgram;
        }
    }
}
