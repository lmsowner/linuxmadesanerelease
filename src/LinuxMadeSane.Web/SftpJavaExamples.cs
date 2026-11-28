// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web;

internal static class SftpJavaExamples
{
    internal const string Connect = """
        import com.google.gson.*;
        import java.io.*;
        import java.net.*;
        import java.net.http.*;
        import java.nio.file.*;
        import java.time.Duration;

        class SftpClient {
            static final HttpClient client = HttpClient.newHttpClient();
            static String text(String file) throws IOException { return Files.readString(Path.of(file)).strip(); }
            static class ApiError extends IOException {
                final int status;
                ApiError(int status) { super("HTTP " + status); this.status = status; }
            }
            static byte[] call(String method, String url, JsonObject body) throws Exception {
                var builder = HttpRequest.newBuilder(URI.create(url)).timeout(Duration.ofSeconds(60))
                    .header("Authorization", "Bearer " + text("token.txt"));
                var data = HttpRequest.BodyPublishers.noBody();
                if (body != null) {
                    builder.header("Content-Type", "application/json");
                    data = HttpRequest.BodyPublishers.ofString(body.toString());
                }
                var response = client.send(builder.method(method, data).build(), HttpResponse.BodyHandlers.ofByteArray());
                if (response.statusCode() / 100 != 2) throw new ApiError(response.statusCode());
                return response.body();
            }
            static JsonElement get(String url) throws Exception {
                return JsonParser.parseString(new String(call("GET", url, null), java.nio.charset.StandardCharsets.UTF_8));
            }
            public static void main(String[] args) throws Exception {
                System.out.println(get("https://sftp.example.net:5443/api/sftpserver/"));
                var status = get("https://sftp.example.net:5443/api/sftpserver/server/status").getAsJsonObject();
                for (String key : new String[]{"managed", "sshActive", "configurationValid"})
                    if (!status.get(key).getAsBoolean()) throw new IOException("Check SFTP readiness: " + key);
            }
        }
        """;

    internal const string Provision = """
        import java.io.IOException;
        import com.google.gson.JsonObject;

        class Provision extends SftpClient {
            static boolean owned() throws Exception {
                return get("https://sftp.example.net:5443/api/sftpserver/users/import_user/exists")
                    .getAsJsonObject().get("exists").getAsBoolean();
            }
            public static void main(String[] args) throws Exception {
                if (!owned()) {
                    String mode = "PublicKeyOnly"; // Or PasswordOnly or PasswordAndPublicKey
                    var body = new JsonObject();
                    body.addProperty("username", "import_user");
                    body.addProperty("authenticationMode", mode);
                    if (!mode.equals("PublicKeyOnly")) body.addProperty("password", text("customer-password.txt"));
                    if (!mode.equals("PasswordOnly")) body.addProperty("publicKey", text("customer.pub"));
                    try { call("POST", "https://sftp.example.net:5443/api/sftpserver/users", body); }
                    catch (ApiError e) { if (e.status != 409 || !owned()) throw e; }
                    catch (IOException e) { if (!owned()) throw e; }
                }
                System.out.println(get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
            }
        }
        """;

    internal const string RegisterKey = """
        import com.google.gson.JsonObject;

        class RegisterKey extends SftpClient {
            public static void main(String[] args) throws Exception {
                var body = new JsonObject();
                body.addProperty("publicKey", text("customer.pub"));
                call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key", body);
                System.out.println(get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
            }
        }
        """;

    internal const string ChangeMode = """
        import com.google.gson.JsonObject;

        class ChangeMode extends SftpClient {
            public static void main(String[] args) throws Exception {
                String target = "PasswordAndPublicKey"; // Or PublicKeyOnly or PasswordOnly
                var account = get("https://sftp.example.net:5443/api/sftpserver/users/import_user").getAsJsonObject();
                String current = account.get("authenticationMode").getAsString();
                if (!current.equals(target)) {
                    if (!target.equals("PasswordOnly") && !account.get("hasPublicKey").getAsBoolean()) {
                        var key = new JsonObject();
                        key.addProperty("publicKey", text("customer.pub"));
                        call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key", key);
                    }
                    var settings = new JsonObject();
                    settings.addProperty("authenticationMode", target);
                    if (current.equals("PublicKeyOnly")) settings.addProperty("password", text("customer-password.txt"));
                    call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth", settings);
                }
                account = get("https://sftp.example.net:5443/api/sftpserver/users/import_user").getAsJsonObject();
                if (!target.equals(account.get("authenticationMode").getAsString()) || !account.get("enabled").getAsBoolean())
                    throw new IllegalStateException("Review the account before sending login instructions.");
                System.out.println(get("https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide"));
            }
        }
        """;

    internal const string Import = """
        import java.nio.file.*;
        import java.util.Arrays;

        class Collect extends SftpClient {
            public static void main(String[] args) throws Exception {
                boolean deleteAfterDownload = false;
                var entries = get("https://sftp.example.net:5443/api/sftpserver/users/import_user/files").getAsJsonArray();
                for (var item : entries) {
                    var file = item.getAsJsonObject();
                    if (file.get("isDirectory").getAsBoolean() || !file.get("name").getAsString().equals("orders-1042.csv")) continue;
                    var bytes = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=orders-1042.csv", null);
                    var local = Path.of("orders-1042.csv");
                    if (!Files.exists(local)) Files.write(local, bytes, StandardOpenOption.CREATE_NEW);
                    if (!Arrays.equals(bytes, Files.readAllBytes(local))) throw new java.io.IOException("Local copy differs. Source retained.");
                    if (deleteAfterDownload)
                        call("DELETE", "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=orders-1042.csv", null);
                }
            }
        }
        """;

    internal const string Offboard = """
        import java.util.Scanner;

        class Offboard extends SftpClient {
            public static void main(String[] args) throws Exception {
                System.out.print("Action for import_user (disable, enable or DELETE import_user): ");
                switch (new Scanner(System.in).nextLine()) {
                    case "disable" -> call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable", null);
                    case "enable" -> call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable", null);
                    case "DELETE import_user" -> call("DELETE", "https://sftp.example.net:5443/api/sftpserver/users/import_user", null);
                    default -> System.out.println("No change made.");
                }
            }
        }
        """;
}
