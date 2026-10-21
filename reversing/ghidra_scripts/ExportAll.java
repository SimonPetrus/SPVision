// Dump imports, exports, defined strings and decompiled C for every function.
//@category Export
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.data.StringDataInstance;
import java.io.*;

public class ExportAll extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outDir = getScriptArgs()[0];
        String name = currentProgram.getName().replaceAll("[^A-Za-z0-9._-]", "_");
        new File(outDir).mkdirs();

        try (PrintWriter w = new PrintWriter(new FileWriter(new File(outDir, name + ".imports.txt")))) {
            for (Symbol s : currentProgram.getSymbolTable().getExternalSymbols()) {
                w.println(s.getParentNamespace().getName() + "!" + s.getName());
            }
        }
        try (PrintWriter w = new PrintWriter(new FileWriter(new File(outDir, name + ".exports.txt")))) {
            SymbolIterator it = currentProgram.getSymbolTable().getAllSymbols(true);
            for (Symbol s : it) {
                if (s.isExternalEntryPoint()) w.println(s.getAddress() + " " + s.getName(true));
            }
        }
        try (PrintWriter w = new PrintWriter(new OutputStreamWriter(new FileOutputStream(new File(outDir, name + ".strings.txt")), "UTF-8"))) {
            for (Data d : currentProgram.getListing().getDefinedData(true)) {
                StringDataInstance sdi = StringDataInstance.getStringDataInstance(d);
                if (sdi != StringDataInstance.NULL_INSTANCE) {
                    String v = sdi.getStringValue();
                    if (v != null && v.length() >= 4) w.println(d.getAddress() + "\t" + v.replace("\n", "\\n").replace("\r", "\\r"));
                }
            }
        }
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        try (PrintWriter w = new PrintWriter(new OutputStreamWriter(new FileOutputStream(new File(outDir, name + ".decomp.c")), "UTF-8"))) {
            FunctionIterator fi = currentProgram.getFunctionManager().getFunctions(true);
            for (Function f : fi) {
                if (monitor.isCancelled()) break;
                if (f.isThunk() || f.isExternal()) continue;
                DecompileResults r = di.decompileFunction(f, 60, monitor);
                w.println("// ==== " + f.getName(true) + " @ " + f.getEntryPoint());
                if (r != null && r.decompileCompleted()) w.println(r.getDecompiledFunction().getC());
                else w.println("// decompile failed");
            }
        }
        di.dispose();
    }
}
