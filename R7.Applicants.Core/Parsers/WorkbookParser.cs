﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LiteDB;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using R7.Applicants.Core.Models;

namespace R7.Applicants.Core.Parsers
{
    // TODO: Move regexes to separate configuration class?
    public class WorkbookParser
    {
        ILiteDatabase db;

        ILiteCollection<Division> divisions;
        ILiteCollection<EduForm> eduForms;
        ILiteCollection<EduProgram> eduPrograms;
        ILiteCollection<EduLevel> eduLevels;
        ILiteCollection<Category> categories;
        ILiteCollection<Applicant> applicants;

        IList<Category> categoriesCache;

        public WorkbookParser (ILiteDatabase db)
        {
            this.db = db;

            divisions = db.GetCollection<Division> ("Divisions");
            eduForms = db.GetCollection<EduForm> ("EduForms");
            eduPrograms = db.GetCollection<EduProgram> ("EduPrograms");
            eduLevels = db.GetCollection<EduLevel> ("EduLevels");
            categories = db.GetCollection<Category> ("Categories");
            applicants = db.GetCollection<Applicant> ("Applicants");

            categoriesCache = new List<Category> (categories.FindAll ());
        }

        /// <summary>
        /// Parses the cell, possibly multiple times according to the state changes.
        /// </summary>
        /// <param name="cell">Cell.</param>
        /// <param name="cellStrValue">Cell string value.</param>
        public void ParseCell (ICell cell, string cellStrValue, WorkbookParserContext context, out bool skipRow)
        {
            var parseCellAgain = false;
            do {
                ParseCellOnce (cell, cellStrValue, context, out parseCellAgain, out skipRow);
            } while (parseCellAgain);
        }

        public void ParseCellOnce (ICell cell, string cellStrValue, WorkbookParserContext context, out bool parseCellAgain, out bool skipRow)
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
                else {
                    if (Regex.IsMatch (cellStrValue, "факультет|институт", RegexOptions.IgnoreCase)) {
                        var division = divisions.FindOne (d => d.Title == cellStrValue);
                        if (division == null) {
                            division = new Division {
                                Title = cellStrValue
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
                    if (Regex.IsMatch (cellStrValue, "бакалавриат|специалитет|магистратур|подготовки кадров", RegexOptions.IgnoreCase)) {
                        // TODO: Get edu. level title from predefined values
                        var eduLevelStrValue = Regex.Matches (cellStrValue, "бакалавриат|специалитет|магистратур|подготовки кадров") [0].Value;

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

                        var eduProgramStrValue = Regex.Matches (cellStrValue, "(бакалавриата|специалитета|магистратуры|подготовки кадров высшей квалификации)(.*)", RegexOptions.Singleline | RegexOptions.IgnoreCase) [0].Groups [2].Value;
                        eduProgramStrValue = eduProgramStrValue.Replace ("\n", " ");
                        eduProgramStrValue = eduProgramStrValue.Replace ("\r", " ");
                        eduProgramStrValue = eduProgramStrValue.Replace ("\t", " ");
                        eduProgramStrValue = Regex.Replace (eduProgramStrValue, @"\s+", " ");
                        eduProgramStrValue = eduProgramStrValue.Trim ();

                        var eduProgram = eduPrograms.FindOne (ep => ep.Title == eduProgramStrValue && ep.EduLevelId == context.EduLevel.Id);
                        if (eduProgram == null) {
                            eduProgram = new EduProgram {
                                EduLevelId = context.EduLevel.Id,
                                Title = eduProgramStrValue
                            };
                            var id = eduPrograms.Insert (eduProgram);
                            db.Commit ();
                            eduProgram.Id = id.AsInt32;
                        }
                        context.EduProgram = eduProgram;
                    }
                }
                return;
            }

            if (context.State == WorkbookParserState.TableHeader) {
                if (cell.ColumnIndex == 4) {
                    context.EduProgram.Exam1Title = cellStrValue;
                }
                else if (cell.ColumnIndex == 5) {
                    context.EduProgram.Exam2Title = cellStrValue;
                }
                else if (cell.ColumnIndex == 6) {
                    context.EduProgram.Exam3Title = cellStrValue;
                }
                else if (cellStrValue.Equals ("Категория приема", StringComparison.CurrentCultureIgnoreCase)) {
                    context.State = WorkbookParserState.List;
                    skipRow = true;
                }
                return;
            }

            if (context.State == WorkbookParserState.List) {
                if (cell.IsMergedCell) {
                    context.State = WorkbookParserState.Header;
                    parseCellAgain = true;
                    return;
                }

                if (cell.ColumnIndex == 0) {
                    context.Applicant = new Applicant ();
                    if (int.TryParse (cellStrValue, out int order)) {
                        context.Applicant.Order = order;
                    }
                }
                else if (cell.ColumnIndex == 1) {
                    context.Applicant.Name = cellStrValue;
                }
                else if (cell.ColumnIndex == 2) {
                    context.Applicant.OriginalOrCopy = cellStrValue.Equals ("Оригинал", StringComparison.CurrentCultureIgnoreCase);
                }
                else if (cell.ColumnIndex == 3) {
                    context.Applicant.Consent = cellStrValue.Equals ("Да", StringComparison.CurrentCultureIgnoreCase);
                }
                if (cell.ColumnIndex == 4) {
                    if (int.TryParse (cellStrValue, out int exam1Rate)) {
                        context.Applicant.Exam1Rate = exam1Rate;
                    }
                }
                else if (cell.ColumnIndex == 5) {
                    if (int.TryParse (cellStrValue, out int exam2Rate)) {
                        context.Applicant.Exam2Rate = exam2Rate;
                    }
                }
                else if (cell.ColumnIndex == 6) {
                    if (int.TryParse (cellStrValue, out int exam3Rate)) {
                        context.Applicant.Exam3Rate = exam3Rate;
                    }
                }
                else if (cell.ColumnIndex == 7) {
                    if (int.TryParse (cellStrValue, out int paRate)) {
                        context.Applicant.PaRate = paRate;
                    }
                }
                else if (cell.ColumnIndex == 8) {
                    if (int.TryParse (cellStrValue, out int rate)) {
                        context.Applicant.Rate = rate;
                    }
                }
                else if (cell.ColumnIndex == 9) {
                    var category = categoriesCache.FirstOrDefault (d => d.Title == cellStrValue);
                    if (category == null) {
                        category = new Category {
                            Title = cellStrValue
                        };
                        var id = categories.Insert (category);
                        db.Commit ();
                        category.Id = id.AsInt32;
                        categoriesCache.Add (category);
                    }
                    // insert new applicant
                    context.Applicant.CategoryId = category.Id;
                    context.Applicant.EduProgramId = context.EduProgram.Id;
                    applicants.Insert (context.Applicant);
                    skipRow = true;
                }
            }
        }

        public void ParseTo (string path, ILiteDatabase db)
        {
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
                            ParseCell (cell, cellStrValue, context, out bool skipRow);
                            if (skipRow) {
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
}
