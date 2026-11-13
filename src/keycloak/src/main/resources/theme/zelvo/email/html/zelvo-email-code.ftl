<#import "template.ftl" as layout>
<@layout.emailLayout>
${kcSanitize(msg("zelvoEmailCodeBodyHtml", code, ttlMinutes, realmName))?no_esc}
</@layout.emailLayout>
