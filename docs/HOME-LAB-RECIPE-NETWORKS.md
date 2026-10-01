# Recipe network recommendations

Home Lab prompt recipes in `catalog/home-lab-prompt-recipes.json` may declare a `networkRecommendation` object. The same fields are used by the packaged fallback catalog. These are recommendations, not extra deployment restrictions. Schema version 2 remains compatible with older clients, which ignore the additional field.

```json
"networkRecommendation": {
  "listenInterface": "lan",
  "listenReason": "Use the LAN so local media clients can reach Jellyfin.",
  "outboundRoute": "direct",
  "outboundReason": "Use the normal host route for LAN playback and any metadata requests."
}
```

- `listenInterface`: `lan` or `tailnet`. LMS suggests an actual active host address. Tailnet advice falls back to a private LAN address when no Tailnet interface is present. Multiple suitable networks require a choice unless one has a gateway. Public addresses and container/VPN interfaces are never automatically chosen as a LAN recommendation.
- `listenReason`: a short explanation of which clients need to reach the service. The listener controls incoming access; it does not choose the outbound interface.
- `outboundRoute`: `direct` or `vpn`. Direct follows the host routing table for LAN and Internet destinations; it does not pin traffic to one interface or make the app LAN-only. VPN means an existing LMS VPN Gateway. A single healthy gateway may be suggested; multiple gateways require a choice.
- `outboundReason`: explain why this route suits the recipe. For mixed stacks, name the VPN-routed apps and those that stay direct. Recommendations must agree with the recipe's required VPN and supported app capabilities.

Avoid storing host IP addresses, physical interface names, gateway IDs or secrets in recipes. LMS resolves these on the host and keeps the selected values editable. Existing VPN requirements still apply. Recipes without this object receive compatible LAN/direct or LAN/VPN fallback advice.
