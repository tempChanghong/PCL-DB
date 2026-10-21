import java.io.File;
import java.lang.reflect.InvocationTargetException;

// Calls the same OptiFine installer used by PCL, with an explicit staging root.
// Compile with javac --release 8. No compiler is required on the user's computer.
public final class OptiFineBridge {
    public static void main(String[] args) throws Exception {
        if (args.length != 1) throw new IllegalArgumentException("Expected staging game directory");
        try {
            Class.forName("optifine.Installer").getMethod("doInstall", File.class)
                .invoke(null, new File(args[0]));
        } catch (InvocationTargetException e) {
            e.getCause().printStackTrace();
            System.exit(1);
        }
        System.out.println("OptiFine installation complete");
    }
}
