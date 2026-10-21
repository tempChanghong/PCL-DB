import java.nio.file.Files;
import java.nio.file.Paths;
public final class FixtureProcessor {
    public static void main(String[] args) throws Exception {
        if (args[0].equals("--wait")) { Thread.sleep(60000); return; }
        if (args[0].equals("--fail")) { System.err.println("fixture processor failure"); System.exit(7); }
        for (int i = 0; i < args.length; i += 2) {
            Files.createDirectories(Paths.get(args[i]).getParent());
            Files.write(Paths.get(args[i]), args[i + 1].getBytes("UTF-8"));
        }
        System.out.println("fixture processor complete");
        System.err.println("fixture stderr drained");
    }
}
