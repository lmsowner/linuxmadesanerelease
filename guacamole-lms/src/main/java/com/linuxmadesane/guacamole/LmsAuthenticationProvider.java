/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE for details. */
package com.linuxmadesane.guacamole;

import java.io.*;
import java.net.StandardProtocolFamily;
import java.net.UnixDomainSocketAddress;
import java.nio.channels.Channels;
import java.nio.channels.SocketChannel;
import java.nio.ByteBuffer;
import java.util.concurrent.locks.LockSupport;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import com.fasterxml.jackson.databind.*;
import org.apache.guacamole.GuacamoleException;
import org.apache.guacamole.GuacamoleSecurityException;
import org.apache.guacamole.net.auth.*;
import org.apache.guacamole.net.auth.simple.SimpleUserContext;
import org.apache.guacamole.protocol.GuacamoleConfiguration;

/** A trusted connection bridge. Identity and every authorisation decision remain in LMS. */
public final class LmsAuthenticationProvider extends AbstractAuthenticationProvider {
    private static final ObjectMapper JSON = new ObjectMapper();
    @Override public String getIdentifier() { return "lms"; }
    @Override public AuthenticatedUser authenticateUser(Credentials credentials) throws GuacamoleException {
        var request = credentials.getRequest();
        String grant = request.getHeader("X-LMS-Launch-Grant");
        if (grant == null || grant.length() != 64) return null;
        Map<String,String> data = new HashMap<>();
        data.put("grant", grant);
        for (String field : List.of("sessionId", "userId", "connectionId")) {
            String value = request.getHeader("X-LMS-" + field);
            if (value == null) throw new GuacamoleSecurityException("Invalid LMS launch.");
            data.put(field,value);
        }
        JsonNode result = bridge("/redeem", data);
        var config = new GuacamoleConfiguration();
        config.setProtocol(result.path("protocol").asText());
        if (!Set.of("rdp", "vnc").contains(config.getProtocol())) throw new GuacamoleSecurityException("Invalid LMS connection.");
        result.path("parameters").fields().forEachRemaining(p -> config.setParameter(p.getKey(),p.getValue().asText()));
        return new LmsUser(credentials, data.get("sessionId"), result.path("userLabel").asText(),
            Map.of(data.get("connectionId"),config));
    }
    @Override public UserContext getUserContext(AuthenticatedUser authenticated) throws GuacamoleException {
        LmsUser user = require(authenticated);
        return new SimpleUserContext(this, user.getIdentifier(), user.configurations, false);
    }
    @Override public AuthenticatedUser updateAuthenticatedUser(AuthenticatedUser authenticated, Credentials credentials) throws GuacamoleException {
        LmsUser user = require(authenticated);
        bridge("/validate", Map.of("sessionId",user.session));
        return user;
    }
    private LmsUser require(AuthenticatedUser user) throws GuacamoleException {
        if (!(user instanceof LmsUser)) throw new GuacamoleSecurityException("LMS session required.");
        return (LmsUser)user;
    }
    private static JsonNode bridge(String path, Map<String,String> data) throws GuacamoleException {
        // Unix-domain HTTP is never routed over a public TCP port. The key is a service
        // credential, provisioned through Docker stdin; target passwords are never written.
        try (SocketChannel socket = SocketChannel.open(StandardProtocolFamily.UNIX)) {
            String key = Files.readString(Path.of(Optional.ofNullable(System.getenv("LMS_BRIDGE_KEY_FILE")).orElse("/tmp/lms-bridge-key")), StandardCharsets.UTF_8).trim();
            socket.configureBlocking(false);
            long deadline = System.nanoTime() + 10_000_000_000L;
            socket.connect(UnixDomainSocketAddress.of(Optional.ofNullable(System.getenv("LMS_BRIDGE_SOCKET")).orElse("/run/lms-bridge/bridge.sock")));
            while (!socket.finishConnect()) pause(deadline);
            byte[] body = JSON.writeValueAsBytes(data);
            OutputStream out = new OutputStream() {
                public void write(int value) throws IOException { write(new byte[] { (byte)value }); }
                public void write(byte[] bytes, int offset, int length) throws IOException {
                    var buffer = ByteBuffer.wrap(bytes,offset,length);
                    while (buffer.hasRemaining()) { if (socket.write(buffer) == 0) pause(deadline); }
                }
            };
            out.write(("POST " + path + " HTTP/1.1\r\nHost: lms-bridge\r\nAuthorization: Bearer " + key +
                "\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
            out.write(body); out.flush();
            // LMS emits bounded responses with Content-Length, avoiding chunk ambiguity.
            InputStream in = new InputStream() {
                public int read() throws IOException { byte[] one=new byte[1]; return read(one,0,1)==-1?-1:one[0]&255; }
                public int read(byte[] bytes,int offset,int length) throws IOException {
                    if (length == 0) return 0;
                    var buffer=ByteBuffer.wrap(bytes,offset,length); int count;
                    while ((count=socket.read(buffer))==0) pause(deadline);
                    return count;
                }
            };
            ByteArrayOutputStream header = new ByteArrayOutputStream(); int ch; int matched = 0;
            byte[] end = {13,10,13,10};
            while ((ch = in.read()) != -1 && header.size() < 16384) {
                header.write(ch); matched = ch == end[matched] ? matched + 1 : (ch == 13 ? 1 : 0); if (matched == 4) break;
            }
            String headers = header.toString(StandardCharsets.US_ASCII);
            if (!headers.startsWith("HTTP/1.1 200 ")) throw new GuacamoleSecurityException("LMS denied or expired this session.");
            int length = -1;
            for (String line : headers.split("\r\n")) if (line.toLowerCase(Locale.ROOT).startsWith("content-length:")) length = Integer.parseInt(line.substring(15).trim());
            if (length < 2 || length > 65536) throw new IOException("Invalid bridge response.");
            byte[] bytes = in.readNBytes(length);
            if (bytes.length != length) throw new IOException("Incomplete bridge response.");
            return JSON.readTree(bytes);
        }
        catch (GuacamoleException e) { throw e; }
        catch (Exception e) { throw new GuacamoleSecurityException("The internal LMS bridge is unavailable."); }
    }
    private static void pause(long deadline) throws IOException {
        if (System.nanoTime() > deadline || Thread.currentThread().isInterrupted()) throw new IOException("LMS bridge timeout.");
        LockSupport.parkNanos(1_000_000L);
    }
    private final class LmsUser extends AbstractAuthenticatedUser {
        private final Credentials credentials;
        private final String session;
        private final Map<String,GuacamoleConfiguration> configurations;
        LmsUser(Credentials credentials, String session, String label, Map<String,GuacamoleConfiguration> configurations) {
            this.credentials = credentials; this.session = session; this.configurations = configurations; setIdentifier(label);
        }
        @Override public AuthenticationProvider getAuthenticationProvider() { return LmsAuthenticationProvider.this; }
        @Override public Credentials getCredentials() { return credentials; }
        @Override public Set<String> getEffectiveUserGroups() { return Collections.emptySet(); }
        @Override public void invalidate() { configurations.values().forEach(c -> c.setParameter("password", "")); }
    }
}
