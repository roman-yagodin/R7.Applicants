﻿using System;

namespace R7.Applicants.Tests
{
    class Program
    {
        static void Main (string [] args)
        {
            Console.Write (DatabaseDumper.DumpDatabase (TestDatabase.Instance));
        }
    }
}
