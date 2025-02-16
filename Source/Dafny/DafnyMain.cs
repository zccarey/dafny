//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
// Copyright by the contributors to the Dafny Project
// SPDX-License-Identifier: MIT
//
//-----------------------------------------------------------------------------
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using Bpl = Microsoft.Boogie;
using System.Reflection;

namespace Microsoft.Dafny {

  public class IllegalDafnyFile : Exception { }

  public class DafnyFile {
    public bool UseStdin { get; private set; }
    public string FilePath { get; private set; }
    public string CanonicalPath { get; private set; }
    public string BaseName { get; private set; }
    public bool isPrecompiled { get; private set; }
    public string SourceFileName { get; private set; }

    // Returns a canonical string for the given file path, namely one which is the same
    // for all paths to a given file and different otherwise. The best we can do is to
    // make the path absolute -- detecting case and canoncializing symbolic and hard
    // links are difficult across file systems (which may mount parts of other filesystems,
    // with different characteristics) and is not supported by .Net libraries
    public static string Canonicalize(String filePath) {
      return Path.GetFullPath(filePath);
    }
    public static List<string> fileNames(IList<DafnyFile> dafnyFiles) {
      var sourceFiles = new List<string>();
      foreach (DafnyFile f in dafnyFiles) {
        sourceFiles.Add(f.FilePath);
      }
      return sourceFiles;
    }
    public DafnyFile(string filePath, bool useStdin = false) {
      UseStdin = useStdin;
      FilePath = filePath;
      BaseName = Path.GetFileName(filePath);

      var extension = useStdin ? ".dfy" : Path.GetExtension(filePath);
      if (extension != null) { extension = extension.ToLower(); }

      // Normalizing symbolic links appears to be not
      // supported in .Net APIs, because it is very difficult in general
      // So we will just use the absolute path, lowercased for all file systems.
      // cf. IncludeComparer.CompareTo
      CanonicalPath = Canonicalize(filePath);

      if (!useStdin && !Path.IsPathRooted(filePath)) {
        filePath = Path.GetFullPath(filePath);
      }

      if (extension == ".dfy" || extension == ".dfyi") {
        isPrecompiled = false;
        SourceFileName = filePath;
      } else if (extension == ".dll") {
        isPrecompiled = true;
        var asm = Assembly.LoadFile(filePath);
        string sourceText = null;
        foreach (var adata in asm.CustomAttributes) {
          if (adata.Constructor.DeclaringType.Name == "DafnySourceAttribute") {
            foreach (var args in adata.ConstructorArguments) {
              if (args.ArgumentType.FullName == "System.String") {
                sourceText = (string)args.Value;
              }
            }
          }
        }

        if (sourceText == null) { throw new IllegalDafnyFile(); }
        SourceFileName = Path.GetTempFileName();
        File.WriteAllText(SourceFileName, sourceText);

      } else {
        throw new IllegalDafnyFile();
      }


    }
  }

  public class Main {

    public static void MaybePrintProgram(Program program, string filename, bool afterResolver) {
      if (filename == null) {
        return;
      }

      var tw = filename == "-" ? Console.Out : new StreamWriter(filename);
      var pr = new Printer(tw, DafnyOptions.O.PrintMode);
      pr.PrintProgram(program, afterResolver);
    }

    public static void FinitizeProgram(Program program, string filename) {
      if (filename == null) {
        return;
      }
      
      var tw = filename == "-" ? Console.Out : new StreamWriter(filename);
      var vmtPr = new VMTPrinter(tw, DafnyOptions.O.DafnyFinitizedDatatypes);
      vmtPr.PrintProgram(program, true);
    }

    /// <summary>
    /// Returns null on success, or an error string otherwise.
    /// </summary>
    public static string ParseCheck(IList<DafnyFile/*!*/>/*!*/ files, string/*!*/ programName, ErrorReporter reporter, out Program program)
    //modifies Bpl.CommandLineOptions.Clo.XmlSink.*;
    {
      string err = Parse(files, programName, reporter, out program);
      if (err != null) {
        return err;
      }

      return Resolve(program, reporter);
    }

    public static string Parse(IList<DafnyFile> files, string programName, ErrorReporter reporter, out Program program) {
      Contract.Requires(programName != null);
      Contract.Requires(files != null);
      program = null;
      ModuleDecl module = new LiteralModuleDecl(new DefaultModuleDecl(), null);
      BuiltIns builtIns = new BuiltIns();

      foreach (DafnyFile dafnyFile in files) {
        Contract.Assert(dafnyFile != null);
        if (Bpl.CommandLineOptions.Clo.XmlSink != null && Bpl.CommandLineOptions.Clo.XmlSink.IsOpen && !dafnyFile.UseStdin) {
          Bpl.CommandLineOptions.Clo.XmlSink.WriteFileFragment(dafnyFile.FilePath);
        }
        if (Bpl.CommandLineOptions.Clo.Trace) {
          Console.WriteLine("Parsing " + dafnyFile.FilePath);
        }

        string err = ParseFile(dafnyFile, null, module, builtIns, new Errors(reporter), !dafnyFile.isPrecompiled, !dafnyFile.isPrecompiled);
        if (err != null) {
          return err;
        }
      }

      if (!(DafnyOptions.O.DisallowIncludes || DafnyOptions.O.PrintIncludesMode == DafnyOptions.IncludesModes.Immediate)) {
        string errString = ParseIncludes(module, builtIns, DafnyFile.fileNames(files), new Errors(reporter));
        if (errString != null) {
          return errString;
        }
      }

      if (DafnyOptions.O.PrintIncludesMode == DafnyOptions.IncludesModes.Immediate) {
        DependencyMap dmap = new DependencyMap();
        dmap.AddIncludes(((LiteralModuleDecl)module).ModuleDef.Includes);
        dmap.PrintMap();
      }

      program = new Program(programName, module, builtIns, reporter);

      MaybePrintProgram(program, DafnyOptions.O.DafnyPrintFile, false);

      return null; // success
    }

    public static string Resolve(Program program, ErrorReporter reporter) {
      if (Bpl.CommandLineOptions.Clo.NoResolve || Bpl.CommandLineOptions.Clo.NoTypecheck) { return null; }

      Dafny.Resolver r = new Dafny.Resolver(program);
      r.ResolveProgram(program);
      MaybePrintProgram(program, DafnyOptions.O.DafnyPrintResolvedFile, true);

      // Dafinite invocation
      if (DafnyOptions.O.DafnyFinitizedDatatypes.Count != 0) {
        FinitizeProgram(program, DafnyOptions.O.DafnyPrintFinitizedVMTFile);
      }
      
      if (reporter.Count(ErrorLevel.Error) != 0) {
        return string.Format("{0} resolution/type errors detected in {1}", reporter.Count(ErrorLevel.Error), program.Name);
      }

      return null;  // success
    }

    // Lower-case file names before comparing them, since Windows uses case-insensitive file names
    private class IncludeComparer : IComparer<Include> {
      public int Compare(Include x, Include y) {
        return x.CompareTo(y);
      }
    }

    public static string ParseIncludes(ModuleDecl module, BuiltIns builtIns, IList<string> excludeFiles, Errors errs) {
      SortedSet<Include> includes = new SortedSet<Include>(new IncludeComparer());
      DependencyMap dmap = new DependencyMap();
      foreach (string fileName in excludeFiles) {
        includes.Add(new Include(null, null, fileName));
      }
      dmap.AddIncludes(includes);
      bool newlyIncluded;
      do {
        newlyIncluded = false;

        List<Include> newFilesToInclude = new List<Include>();
        dmap.AddIncludes(((LiteralModuleDecl)module).ModuleDef.Includes);
        foreach (Include include in ((LiteralModuleDecl)module).ModuleDef.Includes) {
          bool isNew = includes.Add(include);
          if (isNew) {
            newlyIncluded = true;
            newFilesToInclude.Add(include);
          }
        }

        foreach (Include include in newFilesToInclude) {
          DafnyFile file;
          try { file = new DafnyFile(include.includedFilename); } catch (IllegalDafnyFile) {
            return (String.Format("Include of file \"{0}\" failed.", include.includedFilename));
          }
          string ret = ParseFile(file, include, module, builtIns, errs, false);
          if (ret != null) {
            return ret;
          }
        }
      } while (newlyIncluded);


      if (DafnyOptions.O.PrintIncludesMode != DafnyOptions.IncludesModes.None) {
        dmap.PrintMap();
      }

      return null; // Success
    }

    private static string ParseFile(DafnyFile dafnyFile, Include include, ModuleDecl module, BuiltIns builtIns, Errors errs, bool verifyThisFile = true, bool compileThisFile = true) {
      var fn = DafnyOptions.Clo.UseBaseNameForFileName ? Path.GetFileName(dafnyFile.FilePath) : dafnyFile.FilePath;
      try {
        int errorCount = Dafny.Parser.Parse(dafnyFile.UseStdin, dafnyFile.SourceFileName, include, module, builtIns, errs, verifyThisFile, compileThisFile);
        if (errorCount != 0) {
          return string.Format("{0} parse errors detected in {1}", errorCount, fn);
        }
      } catch (IOException e) {
        Bpl.IToken tok = include == null ? Bpl.Token.NoToken : include.tok;
        errs.SemErr(tok, "Unable to open included file");
        return string.Format("Error opening file \"{0}\": {1}", fn, e.Message);
      }
      return null; // Success
    }

  }
}
